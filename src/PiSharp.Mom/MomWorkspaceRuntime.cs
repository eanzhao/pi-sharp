using System.Collections.Concurrent;

namespace PiSharp.Mom;

public sealed class MomWorkspaceRuntime
{
    private readonly ConcurrentDictionary<string, ChannelState> _channelStates = new(StringComparer.Ordinal);
    private readonly ISlackMessagingClient _slackClient;
    private readonly MomTurnProcessor _turnProcessor;
    private readonly MomChannelStore _store;

    public MomWorkspaceRuntime(
        MomTurnProcessor turnProcessor,
        ISlackMessagingClient slackClient,
        MomChannelStore store)
    {
        _turnProcessor = turnProcessor ?? throw new ArgumentNullException(nameof(turnProcessor));
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task DispatchAsync(SlackIncomingEvent incomingEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        if (incomingEvent.ShouldLogToChannelLog)
        {
            await _store.LogIncomingEventAsync(incomingEvent, cancellationToken).ConfigureAwait(false);
        }

        if (!incomingEvent.RequiresResponse)
        {
            return;
        }

        var normalizedPrompt = MomTurnProcessor.NormalizePrompt(incomingEvent);
        var state = _channelStates.GetOrAdd(incomingEvent.ChannelId, static _ => new ChannelState());
        var shouldNotifyBusy = false;

        if (string.Equals(normalizedPrompt, "stop", StringComparison.OrdinalIgnoreCase))
        {
            CancellationTokenSource? cancellationTokenSource;
            lock (state.SyncRoot)
            {
                cancellationTokenSource = state.CancellationTokenSource;
            }

            if (cancellationTokenSource is null)
            {
                await _slackClient.PostMessageAsync(
                        incomingEvent.ChannelId,
                        "_Nothing running._",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            cancellationTokenSource.Cancel();
            await _slackClient.PostMessageAsync(
                incomingEvent.ChannelId,
                "_Stop requested._",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.ActiveTask is not null && !state.ActiveTask.IsCompleted)
            {
                if (incomingEvent.QueueIfBusy)
                {
                    if (state.PendingEvents.Count >= MomDefaults.MaxQueuedEventsPerChannel)
                    {
                        return;
                    }

                    state.PendingEvents.Enqueue(incomingEvent);
                    return;
                }

                shouldNotifyBusy = true;
            }
            else
            {
                state.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                state.ActiveTask = RunChannelTurnAsync(state, incomingEvent);
            }
        }

        if (shouldNotifyBusy)
        {
            await _slackClient.PostMessageAsync(
                    incomingEvent.ChannelId,
                    "_Already working. Send `stop` to cancel._",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task WaitForIdleAsync(string channelId)
    {
        if (_channelStates.TryGetValue(channelId, out var state))
        {
            lock (state.SyncRoot)
            {
                return state.ActiveTask ?? Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }

    private async Task RunChannelTurnAsync(ChannelState state, SlackIncomingEvent incomingEvent)
    {
        CancellationTokenSource? cancellationTokenSource;
        lock (state.SyncRoot)
        {
            cancellationTokenSource = state.CancellationTokenSource;
        }

        try
        {
            await _turnProcessor.ProcessAsync(incomingEvent, cancellationTokenSource?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            SlackIncomingEvent? nextEvent = null;

            lock (state.SyncRoot)
            {
                state.CancellationTokenSource?.Dispose();
                state.CancellationTokenSource = null;

                if (state.PendingEvents.Count > 0)
                {
                    nextEvent = state.PendingEvents.Dequeue();
                    state.CancellationTokenSource = new CancellationTokenSource();
                    state.ActiveTask = RunChannelTurnAsync(state, nextEvent);
                }
                else
                {
                    state.ActiveTask = null;
                }
            }
        }
    }

    private sealed class ChannelState
    {
        public object SyncRoot { get; } = new();

        public CancellationTokenSource? CancellationTokenSource { get; set; }

        public Task? ActiveTask { get; set; }

        public Queue<SlackIncomingEvent> PendingEvents { get; } = new();
    }
}
