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
    public async Task RefreshAsync_PersistsSnapshotAndReloadsIt()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, MomDefaults.SlackMetadataFileName);
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

        try
        {
            var firstIndex = new MomSlackWorkspaceIndex();
            using (var service = new MomSlackMetadataService(metadataClient, firstIndex, persistencePath: filePath))
            {
                await service.RefreshAsync();
            }

            Assert.True(File.Exists(filePath));

            var secondIndex = new MomSlackWorkspaceIndex();
            using var reloaded = new MomSlackMetadataService(
                new FakeSlackWorkspaceMetadataClient(),
                secondIndex,
                persistencePath: filePath);

            Assert.Equal("alice", secondIndex.FindUser("U123")?.UserName);
            Assert.Equal("general", secondIndex.FindChannel("C123")?.Name);
            Assert.NotEqual(DateTimeOffset.MinValue, reloaded.LastRefreshAt);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
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
