using System.Collections.Concurrent;

namespace PiSharp.Mom;

public sealed class MomWorkspaceRuntime
{
    private readonly ConcurrentDictionary<string, ChannelState> _channelStates = new(StringComparer.Ordinal);
    private readonly ISlackMessagingClient _slackClient;
    private readonly MomTurnProcessor _turnProcessor;
    private readonly MomChannelStore _store;
    private readonly MomSlackMetadataService? _metadataService;
    private readonly MomLogBackfiller? _backfiller;
    private readonly string? _botUserId;

    public MomWorkspaceRuntime(
        MomTurnProcessor turnProcessor,
        ISlackMessagingClient slackClient,
        MomChannelStore store,
        MomSlackMetadataService? metadataService = null,
        MomLogBackfiller? backfiller = null,
        string? botUserId = null)
    {
        _turnProcessor = turnProcessor ?? throw new ArgumentNullException(nameof(turnProcessor));
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _metadataService = metadataService;
        _backfiller = backfiller;
        _botUserId = string.IsNullOrWhiteSpace(botUserId) ? null : botUserId.Trim();
    }

    public async Task DispatchAsync(SlackIncomingEvent incomingEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        if (_metadataService is not null &&
            !string.Equals(incomingEvent.UserId, "EVENT", StringComparison.Ordinal))
        {
            try
            {
                await _metadataService.RefreshIfNeededAsync(
                        incomingEvent.UserId,
                        incomingEvent.ChannelId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Metadata refresh is best-effort; fall back to raw Slack IDs for the current turn.
            }
        }

        if (_backfiller is not null &&
            _botUserId is not null &&
            !string.Equals(incomingEvent.UserId, "EVENT", StringComparison.Ordinal) &&
            !File.Exists(_store.GetLogFilePath(incomingEvent.ChannelId)))
        {
            try
            {
                await _backfiller.BackfillRecentHistoryAsync(
                        incomingEvent.ChannelId,
                        _botUserId,
                        incomingEvent.Timestamp,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // History bootstrap is best-effort; continue with the live event even if Slack backfill fails.
            }
        }

        if (incomingEvent.ShouldLogToChannelLog)
        {
            var logResult = await _store.LogIncomingEventAsync(incomingEvent, cancellationToken).ConfigureAwait(false);
            if (logResult.IsDuplicate)
            {
                return;
            }
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
