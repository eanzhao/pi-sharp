using System.Collections.Concurrent;
using System.Globalization;

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
    private readonly Func<string, CancellationToken, Task>? _reportNoticeAsync;
    private readonly MomRuntimeStats? _runtimeStats;

    public MomWorkspaceRuntime(
        MomTurnProcessor turnProcessor,
        ISlackMessagingClient slackClient,
        MomChannelStore store,
        MomSlackMetadataService? metadataService = null,
        MomLogBackfiller? backfiller = null,
        string? botUserId = null,
        Func<string, CancellationToken, Task>? reportNoticeAsync = null,
        MomRuntimeStats? runtimeStats = null)
    {
        _turnProcessor = turnProcessor ?? throw new ArgumentNullException(nameof(turnProcessor));
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _metadataService = metadataService;
        _backfiller = backfiller;
        _botUserId = string.IsNullOrWhiteSpace(botUserId) ? null : botUserId.Trim();
        _reportNoticeAsync = reportNoticeAsync;
        _runtimeStats = runtimeStats;
    }

    public async Task DispatchAsync(SlackIncomingEvent incomingEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);
        var state = _channelStates.GetOrAdd(incomingEvent.ChannelId, static _ => new ChannelState());

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

        await EnsureHistoryContinuityAsync(state, incomingEvent, cancellationToken).ConfigureAwait(false);

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

    private async Task EnsureHistoryContinuityAsync(
        ChannelState state,
        SlackIncomingEvent incomingEvent,
        CancellationToken cancellationToken)
    {
        if (_backfiller is null ||
            _botUserId is null ||
            string.Equals(incomingEvent.UserId, "EVENT", StringComparison.Ordinal))
        {
            return;
        }

        var logPath = _store.GetLogFilePath(incomingEvent.ChannelId);
        var channelDescription = DescribeChannel(incomingEvent.ChannelId);
        if (!File.Exists(logPath))
        {
            try
            {
                var messagesLogged = await _backfiller.BackfillRecentHistoryAsync(
                        incomingEvent.ChannelId,
                        _botUserId,
                        incomingEvent.Timestamp,
                        cancellationToken)
                    .ConfigureAwait(false);
                _runtimeStats?.RecordBootstrapBackfill(
                    channelDescription,
                    messagesLogged,
                    DateTimeOffset.UtcNow);
                await ReportNoticeAsync(
                        $"Bootstrap backfill for {channelDescription}: {messagesLogged} messages",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _runtimeStats?.RecordBootstrapBackfillFailure();
                await ReportNoticeAsync(
                        $"Bootstrap backfill failed for {channelDescription}: {exception.Message}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (incomingEvent.ConnectionGeneration <= 0)
        {
            return;
        }

        var latestLoggedTimestamp = _store.GetLatestLoggedTimestamp(incomingEvent.ChannelId);
        if (!ShouldBackfillReconnectGap(latestLoggedTimestamp, incomingEvent.Timestamp))
        {
            return;
        }

        lock (state.SyncRoot)
        {
            if (state.LastReconnectBackfillGeneration >= incomingEvent.ConnectionGeneration)
            {
                return;
            }

            state.LastReconnectBackfillGeneration = incomingEvent.ConnectionGeneration;
        }

        try
        {
            var messagesLogged = await _backfiller.BackfillMissingHistoryAsync(
                    incomingEvent.ChannelId,
                    _botUserId,
                    latestLoggedTimestamp!,
                    incomingEvent.Timestamp,
                    cancellationToken)
                .ConfigureAwait(false);
            _runtimeStats?.RecordReconnectGapBackfill(
                channelDescription,
                messagesLogged,
                DateTimeOffset.UtcNow);
            await ReportNoticeAsync(
                    $"Reconnect gap backfill for {channelDescription} after reconnect #{incomingEvent.ConnectionGeneration}: {messagesLogged} messages",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _runtimeStats?.RecordReconnectGapBackfillFailure();
            await ReportNoticeAsync(
                    $"Reconnect gap backfill failed for {channelDescription} after reconnect #{incomingEvent.ConnectionGeneration}: {exception.Message}",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool ShouldBackfillReconnectGap(string? oldestTimestamp, string latestTimestamp)
    {
        if (string.IsNullOrWhiteSpace(oldestTimestamp) ||
            !TryParseTimestamp(oldestTimestamp, out var oldestValue) ||
            !TryParseTimestamp(latestTimestamp, out var latestValue))
        {
            return false;
        }

        return latestValue > oldestValue;
    }

    private static bool TryParseTimestamp(string value, out double timestamp) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp);

    private string DescribeChannel(string channelId)
    {
        var label = _store.GetChannelLabel(channelId);
        return string.Equals(label, channelId, StringComparison.Ordinal)
            ? channelId
            : $"{label} ({channelId})";
    }

    private async Task ReportNoticeAsync(string message, CancellationToken cancellationToken)
    {
        if (_reportNoticeAsync is null)
        {
            return;
        }

        try
        {
            await _reportNoticeAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Runtime notices are best-effort and should not interfere with turn processing.
        }
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

        public int LastReconnectBackfillGeneration { get; set; } = -1;

        public CancellationTokenSource? CancellationTokenSource { get; set; }

        public Task? ActiveTask { get; set; }

        public Queue<SlackIncomingEvent> PendingEvents { get; } = new();
    }
}
