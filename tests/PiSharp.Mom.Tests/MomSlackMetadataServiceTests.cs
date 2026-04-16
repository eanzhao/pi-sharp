using PiSharp.Mom;
using PiSharp.Mom.Tests.Support;

namespace PiSharp.Mom.Tests;

public sealed class MomSlackMetadataServiceTests
{
    [Fact]
    public async Task RefreshAsync_UpdatesWorkspaceIndex()
    {
        var metadataClient = new FakeSlackWorkspaceMetadataClient();
        metadataClient.EnqueueSnapshot(
            users:
            [
                new SlackUserInfo("U123", "alice", "Alice Example"),
            ],
            channels:
            [
                new SlackChannelInfo("C123", "general"),
                new SlackChannelInfo("D123", "DM:alice"),
            ]);

        var workspaceIndex = new MomSlackWorkspaceIndex();
        using var service = new MomSlackMetadataService(metadataClient, workspaceIndex);

        await service.RefreshAsync();

        Assert.Equal("alice", workspaceIndex.FindUser("U123")?.UserName);
        Assert.Equal("general", workspaceIndex.FindChannel("C123")?.Name);
        Assert.Equal("DM:alice", workspaceIndex.FindChannel("D123")?.Name);
        Assert.Equal(1, metadataClient.GetUsersCallCount);
        Assert.Equal(1, metadataClient.GetChannelsCallCount);
    }

    [Fact]
    public async Task RefreshIfNeededAsync_RefreshesMissingIdsAndExpiredMetadata()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero));
        var metadataClient = new FakeSlackWorkspaceMetadataClient();
        metadataClient.EnqueueSnapshot(
            users:
            [
                new SlackUserInfo("U123", "alice", "Alice Example"),
            ],
            channels:
            [
                new SlackChannelInfo("C123", "general"),
            ]);
        metadataClient.EnqueueSnapshot(
            users:
            [
                new SlackUserInfo("U123", "alice", "Alice Example"),
            ],
            channels:
            [
                new SlackChannelInfo("C123", "team-general"),
            ]);

        var workspaceIndex = new MomSlackWorkspaceIndex();
        using var service = new MomSlackMetadataService(
            metadataClient,
            workspaceIndex,
            timeProvider,
            refreshInterval: TimeSpan.FromMinutes(10));

        var refreshed = await service.RefreshIfNeededAsync("U123", "C123");

        Assert.True(refreshed);
        Assert.Equal("general", workspaceIndex.FindChannel("C123")?.Name);
        Assert.Equal(1, metadataClient.GetUsersCallCount);
        Assert.Equal(1, metadataClient.GetChannelsCallCount);

        refreshed = await service.RefreshIfNeededAsync("U123", "C123");

        Assert.False(refreshed);
        Assert.Equal(1, metadataClient.GetUsersCallCount);
        Assert.Equal(1, metadataClient.GetChannelsCallCount);

        timeProvider.Advance(TimeSpan.FromMinutes(11));
        refreshed = await service.RefreshIfNeededAsync("U123", "C123");

        Assert.True(refreshed);
        Assert.Equal("team-general", workspaceIndex.FindChannel("C123")?.Name);
        Assert.Equal(2, metadataClient.GetUsersCallCount);
        Assert.Equal(2, metadataClient.GetChannelsCallCount);
    }
}
