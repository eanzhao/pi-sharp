using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomRuntimeStatsTests
{
    [Fact]
    public void SnapshotAndFormatSummary_ReportAccumulatedCounts()
    {
        var stats = new MomRuntimeStats();

        stats.RecordStartupBackfill(new MomBackfillResult(2, 5));
        stats.RecordReconnect();
        stats.RecordBootstrapBackfill(3);
        stats.RecordBootstrapBackfillFailure();
        stats.RecordReconnectGapBackfill(4);
        stats.RecordReconnectGapBackfillFailure();

        var snapshot = stats.Snapshot();

        Assert.Equal(2, snapshot.StartupBackfillChannels);
        Assert.Equal(5, snapshot.StartupBackfillMessages);
        Assert.Equal(1, snapshot.ReconnectCount);
        Assert.Equal(1, snapshot.BootstrapBackfillCount);
        Assert.Equal(3, snapshot.BootstrapBackfillMessages);
        Assert.Equal(1, snapshot.BootstrapBackfillFailures);
        Assert.Equal(1, snapshot.ReconnectGapBackfillCount);
        Assert.Equal(4, snapshot.ReconnectGapBackfillMessages);
        Assert.Equal(1, snapshot.ReconnectGapBackfillFailures);

        Assert.Equal(
            "Runtime stats: startup_channels=2 startup_messages=5 reconnects=1 bootstrap_backfills=1 bootstrap_messages=3 bootstrap_failures=1 reconnect_gap_backfills=1 reconnect_gap_messages=4 reconnect_gap_failures=1",
            stats.FormatSummary());
    }

    [Fact]
    public void PersistsAndReloadsAcrossInstances()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-stats-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, MomDefaults.RuntimeStatsFileName);

        try
        {
            var first = new MomRuntimeStats(filePath);
            first.RecordStartupBackfill(new MomBackfillResult(2, 5));
            first.RecordReconnect();
            first.RecordBootstrapBackfill(3);

            Assert.True(File.Exists(filePath));

            var second = new MomRuntimeStats(filePath);
            var secondSnapshot = second.Snapshot();
            Assert.Equal(2, secondSnapshot.StartupBackfillChannels);
            Assert.Equal(5, secondSnapshot.StartupBackfillMessages);
            Assert.Equal(1, secondSnapshot.ReconnectCount);
            Assert.Equal(1, secondSnapshot.BootstrapBackfillCount);
            Assert.Equal(3, secondSnapshot.BootstrapBackfillMessages);

            second.RecordReconnectGapBackfill(4);
            second.RecordBootstrapBackfillFailure();

            var third = new MomRuntimeStats(filePath);
            var thirdSnapshot = third.Snapshot();
            Assert.Equal(1, thirdSnapshot.BootstrapBackfillFailures);
            Assert.Equal(1, thirdSnapshot.ReconnectGapBackfillCount);
            Assert.Equal(4, thirdSnapshot.ReconnectGapBackfillMessages);
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
    public void IgnoresMalformedPersistenceFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-stats-bad-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, MomDefaults.RuntimeStatsFileName);
        File.WriteAllText(filePath, "{this is not valid json");

        try
        {
            var stats = new MomRuntimeStats(filePath);
            var snapshot = stats.Snapshot();

            Assert.Equal(0, snapshot.StartupBackfillChannels);
            Assert.Equal(0, snapshot.ReconnectCount);

            stats.RecordReconnect();

            var reloaded = new MomRuntimeStats(filePath);
            Assert.Equal(1, reloaded.Snapshot().ReconnectCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
