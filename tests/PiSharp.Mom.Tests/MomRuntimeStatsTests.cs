using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomRuntimeStatsTests
{
    [Fact]
    public void SnapshotAndFormatSummary_ReportAccumulatedCounts()
    {
        var startupAt = new DateTimeOffset(2026, 4, 16, 1, 2, 3, TimeSpan.Zero);
        var reconnectAt = new DateTimeOffset(2026, 4, 16, 1, 3, 4, TimeSpan.Zero);
        var bootstrapAt = new DateTimeOffset(2026, 4, 16, 1, 4, 5, TimeSpan.Zero);
        var bootstrapFailureAt = new DateTimeOffset(2026, 4, 16, 1, 4, 6, TimeSpan.Zero);
        var gapAt = new DateTimeOffset(2026, 4, 16, 1, 5, 6, TimeSpan.Zero);
        var gapFailureAt = new DateTimeOffset(2026, 4, 16, 1, 5, 7, TimeSpan.Zero);
        var stats = new MomRuntimeStats();

        stats.RecordStartupBackfill(new MomBackfillResult(2, 5), startupAt);
        stats.RecordReconnect(7, reconnectAt);
        stats.RecordBootstrapBackfill("general (C123)", 3, bootstrapAt);
        stats.RecordBootstrapBackfillFailure("alerts (C456)", bootstrapFailureAt);
        stats.RecordReconnectGapBackfill("general (C123)", 4, gapAt);
        stats.RecordReconnectGapBackfillFailure("alerts (C456)", gapFailureAt);

        var snapshot = stats.Snapshot();

        Assert.Equal(2, snapshot.StartupBackfillChannels);
        Assert.Equal(5, snapshot.StartupBackfillMessages);
        Assert.Equal(startupAt, snapshot.LastStartupBackfillAt);
        Assert.Equal(1, snapshot.ReconnectCount);
        Assert.Equal(reconnectAt, snapshot.LastReconnectAt);
        Assert.Equal(7, snapshot.LastReconnectGeneration);
        Assert.Equal(1, snapshot.BootstrapBackfillCount);
        Assert.Equal(3, snapshot.BootstrapBackfillMessages);
        Assert.Equal(1, snapshot.BootstrapBackfillFailures);
        Assert.Equal(bootstrapAt, snapshot.LastBootstrapBackfillAt);
        Assert.Equal("general (C123)", snapshot.LastBootstrapBackfillChannel);
        Assert.Equal(bootstrapFailureAt, snapshot.LastBootstrapBackfillFailureAt);
        Assert.Equal("alerts (C456)", snapshot.LastBootstrapBackfillFailureChannel);
        Assert.Equal(1, snapshot.ReconnectGapBackfillCount);
        Assert.Equal(4, snapshot.ReconnectGapBackfillMessages);
        Assert.Equal(1, snapshot.ReconnectGapBackfillFailures);
        Assert.Equal(gapAt, snapshot.LastReconnectGapBackfillAt);
        Assert.Equal("general (C123)", snapshot.LastReconnectGapBackfillChannel);
        Assert.Equal(gapFailureAt, snapshot.LastReconnectGapBackfillFailureAt);
        Assert.Equal("alerts (C456)", snapshot.LastReconnectGapBackfillFailureChannel);

        Assert.Equal(
            "Runtime stats: startup_channels=2 startup_messages=5 last_startup_backfill=2026-04-16T01:02:03.0000000+00:00 reconnects=1 last_reconnect=2026-04-16T01:03:04.0000000+00:00 last_reconnect_generation=7 bootstrap_backfills=1 bootstrap_messages=3 bootstrap_failures=1 last_bootstrap_backfill=2026-04-16T01:04:05.0000000+00:00 last_bootstrap_channel=general (C123) last_bootstrap_failure=2026-04-16T01:04:06.0000000+00:00 last_bootstrap_failure_channel=alerts (C456) reconnect_gap_backfills=1 reconnect_gap_messages=4 reconnect_gap_failures=1 last_reconnect_gap_backfill=2026-04-16T01:05:06.0000000+00:00 last_reconnect_gap_channel=general (C123) last_reconnect_gap_failure=2026-04-16T01:05:07.0000000+00:00 last_reconnect_gap_failure_channel=alerts (C456)",
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
            first.RecordStartupBackfill(
                new MomBackfillResult(2, 5),
                new DateTimeOffset(2026, 4, 16, 1, 2, 3, TimeSpan.Zero));
            first.RecordReconnect(
                2,
                new DateTimeOffset(2026, 4, 16, 1, 3, 4, TimeSpan.Zero));
            first.RecordBootstrapBackfill(
                "general (C123)",
                3,
                new DateTimeOffset(2026, 4, 16, 1, 4, 5, TimeSpan.Zero));

            Assert.True(File.Exists(filePath));

            var second = new MomRuntimeStats(filePath);
            var secondSnapshot = second.Snapshot();
            Assert.Equal(2, secondSnapshot.StartupBackfillChannels);
            Assert.Equal(5, secondSnapshot.StartupBackfillMessages);
            Assert.Equal(1, secondSnapshot.ReconnectCount);
            Assert.Equal(2, secondSnapshot.LastReconnectGeneration);
            Assert.Equal(1, secondSnapshot.BootstrapBackfillCount);
            Assert.Equal(3, secondSnapshot.BootstrapBackfillMessages);
            Assert.Equal("general (C123)", secondSnapshot.LastBootstrapBackfillChannel);

            second.RecordReconnectGapBackfill(
                "general (C123)",
                4,
                new DateTimeOffset(2026, 4, 16, 1, 5, 6, TimeSpan.Zero));
            second.RecordBootstrapBackfillFailure(
                "alerts (C456)",
                new DateTimeOffset(2026, 4, 16, 1, 5, 7, TimeSpan.Zero));
            second.RecordReconnectGapBackfillFailure(
                "alerts (C456)",
                new DateTimeOffset(2026, 4, 16, 1, 5, 8, TimeSpan.Zero));

            var third = new MomRuntimeStats(filePath);
            var thirdSnapshot = third.Snapshot();
            Assert.Equal(1, thirdSnapshot.BootstrapBackfillFailures);
            Assert.Equal("alerts (C456)", thirdSnapshot.LastBootstrapBackfillFailureChannel);
            Assert.Equal(1, thirdSnapshot.ReconnectGapBackfillCount);
            Assert.Equal(4, thirdSnapshot.ReconnectGapBackfillMessages);
            Assert.Equal("general (C123)", thirdSnapshot.LastReconnectGapBackfillChannel);
            Assert.Equal(1, thirdSnapshot.ReconnectGapBackfillFailures);
            Assert.Equal("alerts (C456)", thirdSnapshot.LastReconnectGapBackfillFailureChannel);
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

            stats.RecordReconnect(1);

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
