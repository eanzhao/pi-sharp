namespace PiSharp.Mom;

public sealed record SlackIncomingEvent(
    string ChannelId,
    string UserId,
    string Text,
    string Timestamp,
    string EventType,
    bool IsDirectMessage,
    bool QueueIfBusy = false,
    string? StatusText = null);

public sealed record SlackAuthInfo(string UserId);

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
}
