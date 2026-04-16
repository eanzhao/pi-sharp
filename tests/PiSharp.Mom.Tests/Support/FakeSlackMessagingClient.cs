using PiSharp.Mom;

namespace PiSharp.Mom.Tests.Support;

internal sealed class FakeSlackMessagingClient : ISlackMessagingClient
{
    public SlackAuthInfo AuthInfo { get; set; } = new("B123");

    public List<(string ChannelId, string Timestamp, string Text, string? ThreadTimestamp)> Posts { get; } = [];

    public List<(string ChannelId, string Timestamp, string Text)> Updates { get; } = [];

    public List<(string ChannelId, string Timestamp)> Deletes { get; } = [];

    public List<(string ChannelId, string FilePath, string? Title)> Uploads { get; } = [];

    public Task<SlackAuthInfo> AuthenticateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AuthInfo);

    public Task<string> PostMessageAsync(
        string channelId,
        string text,
        string? threadTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        var timestamp = $"{Posts.Count + 1}.000001";
        Posts.Add((channelId, timestamp, text, threadTimestamp));
        return Task.FromResult(timestamp);
    }

    public Task UpdateMessageAsync(
        string channelId,
        string timestamp,
        string text,
        CancellationToken cancellationToken = default)
    {
        Updates.Add((channelId, timestamp, text));
        return Task.CompletedTask;
    }

    public Task DeleteMessageAsync(
        string channelId,
        string timestamp,
        CancellationToken cancellationToken = default)
    {
        Deletes.Add((channelId, timestamp));
        return Task.CompletedTask;
    }

    public Task UploadFileAsync(
        string channelId,
        string filePath,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        Uploads.Add((channelId, filePath, title));
        return Task.CompletedTask;
    }
}
