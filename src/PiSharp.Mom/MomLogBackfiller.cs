using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Mom;

public sealed record MomBackfillResult(int ChannelsScanned, int MessagesLogged);

public sealed class MomLogBackfiller
{
    private static readonly Regex MentionPattern = new("<@[A-Z0-9]+>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly SlackWebApiClient _slackClient;
    private readonly MomChannelStore _store;

    public MomLogBackfiller(SlackWebApiClient slackClient, MomChannelStore store)
    {
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<MomBackfillResult> BackfillAllAsync(
        string botUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);

        var channelIds = _store.EnumerateLoggedChannels().ToArray();
        var totalMessages = 0;

        foreach (var channelId in channelIds)
        {
            totalMessages += await BackfillChannelAsync(
                    channelId,
                    botUserId,
                    oldest: _store.GetLatestLoggedTimestamp(channelId),
                    latest: null,
                    limit: 200,
                    maxPages: MomDefaults.StartupBackfillMaxPages,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return new MomBackfillResult(channelIds.Length, totalMessages);
    }

    public Task<int> BackfillRecentHistoryAsync(
        string channelId,
        string botUserId,
        string latestTimestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestTimestamp);

        return BackfillChannelAsync(
            channelId,
            botUserId,
            oldest: null,
            latest: latestTimestamp,
            limit: MomDefaults.InitialChannelBackfillMessageLimit,
            maxPages: 1,
            cancellationToken);
    }

    public Task<int> BackfillMissingHistoryAsync(
        string channelId,
        string botUserId,
        string oldestTimestamp,
        string latestTimestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldestTimestamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestTimestamp);

        return BackfillChannelAsync(
            channelId,
            botUserId,
            oldest: oldestTimestamp,
            latest: latestTimestamp,
            limit: 200,
            maxPages: MomDefaults.ReconnectBackfillMaxPages,
            cancellationToken);
    }

    private async Task<int> BackfillChannelAsync(
        string channelId,
        string botUserId,
        string? oldest,
        string? latest,
        int limit,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var pageCount = 0;
        string? cursor = null;
        var messagesLogged = 0;

        do
        {
            var page = await _slackClient.GetConversationHistoryAsync(
                    channelId,
                    oldest: oldest,
                    latest: latest,
                    cursor: cursor,
                    limit: limit,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var message in page.Messages
                         .Where(message => IsRelevant(message, botUserId))
                         .OrderBy(message => ParseTimestamp(message.Timestamp)))
            {
                var isBot = string.Equals(message.UserId, botUserId, StringComparison.Ordinal);
                var userId = isBot ? "bot" : message.UserId!;
                var normalizedText = NormalizeText(message.Text);

                var result = await _store.LogSlackMessageAsync(
                        channelId,
                        userId,
                        normalizedText,
                        message.Timestamp,
                        message.Files,
                        isBot,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!result.IsDuplicate)
                {
                    messagesLogged++;
                }
            }

            cursor = page.NextCursor;
            pageCount++;
        }
        while (!string.IsNullOrWhiteSpace(cursor) && pageCount < maxPages);

        return messagesLogged;
    }

    private static bool IsRelevant(SlackConversationHistoryMessage message, string botUserId)
    {
        if (string.IsNullOrWhiteSpace(message.Timestamp))
        {
            return false;
        }

        if (string.Equals(message.UserId, botUserId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(message.BotId))
        {
            return false;
        }

        if (message.Subtype is not null &&
            !string.Equals(message.Subtype, "file_share", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.UserId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(message.Text) || message.Files is { Count: > 0 };
    }

    private static string NormalizeText(string text) =>
        MentionPattern.Replace(text ?? string.Empty, string.Empty).Trim();

    private static double ParseTimestamp(string timestamp) =>
        double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : double.MinValue;
}
