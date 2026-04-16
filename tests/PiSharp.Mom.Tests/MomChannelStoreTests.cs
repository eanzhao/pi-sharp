using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomChannelStoreTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task LogIncomingEventAsync_UsesWorkspaceMetadataForUserNames()
    {
        var workspaceIndex = new MomSlackWorkspaceIndex(
            users:
            [
                new SlackUserInfo("U123", "alice", "Alice Example"),
            ],
            channels:
            [
                new SlackChannelInfo("C123", "general"),
            ]);

        using var store = new MomChannelStore(_workspaceDirectory, workspaceIndex: workspaceIndex);

        await store.LogIncomingEventAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "hello",
            "12345.6789",
            "message",
            IsDirectMessage: false,
            RequiresResponse: false));

        var line = Assert.Single(File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl")));
        Assert.Contains("\"userName\":\"alice\"", line);
        Assert.Contains("\"displayName\":\"Alice Example\"", line);
        Assert.Equal("general", store.GetChannelLabel("C123"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }
}
