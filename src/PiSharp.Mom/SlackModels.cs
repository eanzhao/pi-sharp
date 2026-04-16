namespace PiSharp.Mom;

public sealed record SlackFileReference(
    string Name,
    string? PrivateDownloadUrl = null,
    string? PrivateUrl = null);

public sealed record SlackUserInfo(
    string Id,
    string UserName,
    string DisplayName);

public sealed record SlackChannelInfo(
    string Id,
    string Name);

public sealed record SlackIncomingEvent(
    string ChannelId,
    string UserId,
    string Text,
    string Timestamp,
    string EventType,
    bool IsDirectMessage,
    IReadOnlyList<SlackFileReference>? Files = null,
    bool QueueIfBusy = false,
    string? StatusText = null,
    bool RequiresResponse = true,
    bool ShouldLogToChannelLog = true);

public sealed record SlackAuthInfo(string UserId);

public sealed record SlackConversationHistoryMessage(
    string Timestamp,
    string? UserId,
    string? BotId,
    string Text,
    string? Subtype = null,
    IReadOnlyList<SlackFileReference>? Files = null);

public sealed record SlackConversationHistoryPage(
    IReadOnlyList<SlackConversationHistoryMessage> Messages,
    string? NextCursor = null);

public interface ISlackMessagingClient
{
    Task<SlackAuthInfo> AuthenticateAsync(CancellationToken cancellationToken = default);

    Task<string> PostMessageAsync(
        string channelId,
        string text,
        string? threadTimestamp = null,
        CancellationToken cancellationToken = default);

    Task UpdateMessageAsync(
        string channelId,
        string timestamp,
        string text,
        CancellationToken cancellationToken = default);

    Task DeleteMessageAsync(
        string channelId,
        string timestamp,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        string channelId,
        string filePath,
        string? title = null,
        CancellationToken cancellationToken = default);
}
