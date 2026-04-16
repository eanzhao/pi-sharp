using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Mom;
using PiSharp.Mom.Tests.Support;

namespace PiSharp.Mom.Tests;

public sealed class MomApplicationTests
{
    [Fact]
    public async Task RunAsync_ParsesArgumentsAndInvokesRunner()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        MomCommandLineOptions? captured = null;

        var application = new MomApplication(
            new MomConsoleEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                Directory.GetCurrentDirectory()),
            runBotAsync: (options, _) =>
            {
                captured = options;
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(
            [
                "--provider", "anthropic",
                "--model", "claude-3-7-sonnet-latest",
                "--api-key", "test-key",
                "./mom-data",
            ]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("anthropic", captured!.Provider);
        Assert.Equal("claude-3-7-sonnet-latest", captured.Model);
        Assert.Equal("test-key", captured.ApiKey);
        Assert.Equal("./mom-data", captured.WorkspaceDirectory);
    }

    [Fact]
    public async Task RunAsync_ParsesStatsCommandAndInvokesStatsRunner()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        MomCommandLineOptions? captured = null;

        var application = new MomApplication(
            new MomConsoleEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                Directory.GetCurrentDirectory()),
            runStatsAsync: (options, _) =>
            {
                captured = options;
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(["stats", "--json", "--channel", "C123", "./mom-data"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(MomCommandKind.ShowStats, captured!.Command);
        Assert.True(captured.JsonOutput);
        Assert.Equal("C123", captured.StatsChannelId);
        Assert.Equal("./mom-data", captured.WorkspaceDirectory);
    }

    [Fact]
    public async Task RunAsync_ShowsHelpForNamespacedRoot()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var application = new MomApplication(
            new MomConsoleEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                Directory.GetCurrentDirectory()),
            appName: "pisharp",
            namespaced: true);

        var exitCode = await application.RunAsync(["mom"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("pisharp mom", output.ToString());
    }

    [Fact]
    public async Task RunAsync_ShowsStatsHelp()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var application = new MomApplication(
            new MomConsoleEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                Directory.GetCurrentDirectory()),
            appName: "pisharp",
            namespaced: true);

        var exitCode = await application.RunAsync(["stats", "--help"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("pisharp mom stats", output.ToString());
        Assert.Contains("--channel", output.ToString());
        Assert.Contains("--json", output.ToString());
    }

    [Fact]
    public async Task RunAsync_StatsCommand_PrintsPersistedRuntimeStats()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            var stats = new MomRuntimeStats(Path.Combine(tempDirectory, MomDefaults.RuntimeStatsFileName));
            stats.RecordStartupBackfill(
                new MomBackfillResult(2, 5),
                new DateTimeOffset(2026, 4, 16, 1, 2, 3, TimeSpan.Zero));
            stats.RecordReconnect(
                4,
                new DateTimeOffset(2026, 4, 16, 1, 3, 4, TimeSpan.Zero));
            stats.RecordBootstrapBackfill(
                "general (C123)",
                3,
                new DateTimeOffset(2026, 4, 16, 1, 4, 5, TimeSpan.Zero));
            stats.RecordBootstrapBackfillFailure(
                "alerts (C456)",
                new InvalidOperationException("Slack API 'conversations.history' failed: invalid_auth"),
                new DateTimeOffset(2026, 4, 16, 1, 4, 6, TimeSpan.Zero));
            stats.RecordReconnectGapBackfill(
                "general (C123)",
                4,
                new DateTimeOffset(2026, 4, 16, 1, 5, 6, TimeSpan.Zero));
            stats.RecordReconnectGapBackfillFailure(
                "alerts (C456)",
                new TimeoutException("gap down"),
                new DateTimeOffset(2026, 4, 16, 1, 5, 7, TimeSpan.Zero));

            var application = new MomApplication(
                new MomConsoleEnvironment(
                    new StringReader(string.Empty),
                    output,
                    error,
                    Directory.GetCurrentDirectory()));

            var exitCode = await application.RunAsync(["stats", tempDirectory]);

            Assert.Equal(0, exitCode);
            var rendered = output.ToString();
            Assert.Contains($"Workspace: {Path.GetFullPath(tempDirectory)}", rendered);
            Assert.Contains("Bootstrap backfill totals: count=1 messages=3 failures=1", rendered);
            Assert.Contains("Last bootstrap failure: at=2026-04-16T01:04:06.0000000+00:00 channel=alerts (C456) kind=auth reason=Slack API 'conversations.history' failed: invalid_auth", rendered);
            Assert.Contains("Last reconnect-gap failure: at=2026-04-16T01:05:07.0000000+00:00 channel=alerts (C456) kind=timeout reason=gap down", rendered);
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
    public async Task RunAsync_StatsCommand_CanPrintChannelSummary()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-channel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 1, 5, 0, TimeSpan.Zero));
            MomSlackMetadataSnapshotStore.Save(
                Path.Combine(tempDirectory, MomDefaults.SlackMetadataFileName),
                new DateTimeOffset(2026, 4, 16, 1, 0, 0, TimeSpan.Zero),
                [new SlackUserInfo("U123", "alice", "Alice Example")],
                [new SlackChannelInfo("C123", "general")]);
            using var store = new MomChannelStore(tempDirectory);
            await store.LogMessageAsync(
                "C123",
                new MomLoggedMessage
                {
                    Ts = "12345.1000",
                    User = "U123",
                    Text = "hello",
                    Attachments = [new MomLoggedAttachment("report.txt", "attachments/123_report.txt")],
                });
            File.WriteAllText(Path.Combine(store.GetSessionDirectory("C123"), "session.jsonl"), "{}");
            File.WriteAllText(Path.Combine(store.GetAttachmentsDirectory("C123"), "123_report.txt"), "data");
            File.WriteAllText(Path.Combine(store.GetScratchDirectory("C123"), "note.txt"), "tmp");
            File.WriteAllText(Path.Combine(store.GetChannelDirectory("C123"), MomDefaults.MemoryFileName), "memory");

            var application = new MomApplication(
                new MomConsoleEnvironment(
                    new StringReader(string.Empty),
                    output,
                    error,
                    Directory.GetCurrentDirectory()),
                timeProvider: timeProvider);

            var exitCode = await application.RunAsync(["stats", "--channel", "C123", tempDirectory]);

            Assert.Equal(0, exitCode);
            var rendered = output.ToString();
            Assert.Contains($"No runtime stats found in {Path.GetFullPath(tempDirectory)}", rendered);
            Assert.Contains("Slack metadata: found=True refreshed_at=2026-04-16T01:00:00.0000000+00:00 age=00:05:00 status=fresh users=1 channels=1", rendered);
            Assert.Contains("Channel: general (C123)", rendered);
            Assert.Contains("Log: found=True messages=1 user=1 bot=0 attachments=1", rendered);
            Assert.Contains("Latest logged message: ts=12345.1000", rendered);
            Assert.Contains("user=alice id=U123", rendered);
            Assert.Contains("Local files: sessions=1 attachment_files=1 scratch_files=1 channel_memory=True", rendered);
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
    public async Task RunAsync_StatsCommand_CanPrintJson()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 1, 15, 0, TimeSpan.Zero));
            var stats = new MomRuntimeStats(Path.Combine(tempDirectory, MomDefaults.RuntimeStatsFileName));
            stats.RecordStartupBackfill(
                new MomBackfillResult(2, 5),
                new DateTimeOffset(2026, 4, 16, 1, 2, 3, TimeSpan.Zero));
            stats.RecordBootstrapBackfillFailure(
                "alerts (C456)",
                new TimeoutException("bootstrap timed out"),
                new DateTimeOffset(2026, 4, 16, 1, 4, 6, TimeSpan.Zero));

            MomSlackMetadataSnapshotStore.Save(
                Path.Combine(tempDirectory, MomDefaults.SlackMetadataFileName),
                new DateTimeOffset(2026, 4, 16, 1, 0, 0, TimeSpan.Zero),
                [new SlackUserInfo("U123", "alice", "Alice Example")],
                [new SlackChannelInfo("C123", "general")]);

            var application = new MomApplication(
                new MomConsoleEnvironment(
                    new StringReader(string.Empty),
                    output,
                    error,
                    Directory.GetCurrentDirectory()),
                timeProvider: timeProvider);

            using var store = new MomChannelStore(tempDirectory);
            await store.LogMessageAsync(
                "C123",
                new MomLoggedMessage
                {
                    Ts = "12345.3000",
                    User = "U123",
                    Text = "channel note",
                });
            File.WriteAllText(Path.Combine(store.GetSessionDirectory("C123"), "session.jsonl"), "{}");

            var exitCode = await application.RunAsync(["stats", "--json", "--channel", "C123", tempDirectory]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            Assert.Equal(Path.GetFullPath(tempDirectory), root.GetProperty("workspaceDirectory").GetString());
            Assert.True(root.GetProperty("runtimeStatsFound").GetBoolean());
            Assert.Contains("Runtime stats:", root.GetProperty("summary").GetString());
            var snapshot = root.GetProperty("snapshot");
            Assert.Equal(2, snapshot.GetProperty("startupBackfillChannels").GetInt32());
            Assert.Equal("alerts (C456)", snapshot.GetProperty("lastBootstrapBackfillFailureChannel").GetString());
            Assert.Equal("timeout", snapshot.GetProperty("lastBootstrapBackfillFailureKind").GetString());
            Assert.Equal("bootstrap timed out", snapshot.GetProperty("lastBootstrapBackfillFailureReason").GetString());
            var slackMetadata = root.GetProperty("slackMetadata");
            Assert.True(slackMetadata.GetProperty("found").GetBoolean());
            Assert.Equal("2026-04-16T01:00:00+00:00", slackMetadata.GetProperty("refreshedAt").GetString());
            Assert.Equal(900, slackMetadata.GetProperty("ageSeconds").GetInt32());
            Assert.Equal("stale", slackMetadata.GetProperty("status").GetString());
            Assert.Equal(1, slackMetadata.GetProperty("userCount").GetInt32());
            Assert.Equal(1, slackMetadata.GetProperty("channelCount").GetInt32());
            var channel = root.GetProperty("channel");
            Assert.Equal("C123", channel.GetProperty("channelId").GetString());
            Assert.Equal("general", channel.GetProperty("channelName").GetString());
            Assert.Equal("general (C123)", channel.GetProperty("channelLabel").GetString());
            Assert.True(channel.GetProperty("logFound").GetBoolean());
            Assert.Equal(1, channel.GetProperty("totalMessages").GetInt32());
            Assert.Equal("U123", channel.GetProperty("latestUserId").GetString());
            Assert.Equal("alice", channel.GetProperty("latestUserLabel").GetString());
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
    public async Task RunAsync_StatsCommand_JsonOutputReportsMissingStatsFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            var application = new MomApplication(
                new MomConsoleEnvironment(
                    new StringReader(string.Empty),
                    output,
                    error,
                    Directory.GetCurrentDirectory()));

            var exitCode = await application.RunAsync(["stats", "--json", tempDirectory]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            var root = document.RootElement;
            Assert.Equal(Path.GetFullPath(tempDirectory), root.GetProperty("workspaceDirectory").GetString());
            Assert.False(root.GetProperty("runtimeStatsFound").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("summary").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("snapshot").ValueKind);
            var slackMetadata = root.GetProperty("slackMetadata");
            Assert.False(slackMetadata.GetProperty("found").GetBoolean());
            Assert.Equal(JsonValueKind.Null, slackMetadata.GetProperty("refreshedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, slackMetadata.GetProperty("ageSeconds").ValueKind);
            Assert.Equal(JsonValueKind.Null, slackMetadata.GetProperty("status").ValueKind);
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
