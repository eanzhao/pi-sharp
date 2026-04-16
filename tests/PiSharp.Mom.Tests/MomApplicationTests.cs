using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Mom;

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

        var exitCode = await application.RunAsync(["stats", "--json", "./mom-data"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(MomCommandKind.ShowStats, captured!.Command);
        Assert.True(captured.JsonOutput);
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
    public async Task RunAsync_StatsCommand_CanPrintJson()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            var stats = new MomRuntimeStats(Path.Combine(tempDirectory, MomDefaults.RuntimeStatsFileName));
            stats.RecordStartupBackfill(
                new MomBackfillResult(2, 5),
                new DateTimeOffset(2026, 4, 16, 1, 2, 3, TimeSpan.Zero));
            stats.RecordBootstrapBackfillFailure(
                "alerts (C456)",
                new TimeoutException("bootstrap timed out"),
                new DateTimeOffset(2026, 4, 16, 1, 4, 6, TimeSpan.Zero));

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
            Assert.True(root.GetProperty("runtimeStatsFound").GetBoolean());
            Assert.Contains("Runtime stats:", root.GetProperty("summary").GetString());
            var snapshot = root.GetProperty("snapshot");
            Assert.Equal(2, snapshot.GetProperty("startupBackfillChannels").GetInt32());
            Assert.Equal("alerts (C456)", snapshot.GetProperty("lastBootstrapBackfillFailureChannel").GetString());
            Assert.Equal("timeout", snapshot.GetProperty("lastBootstrapBackfillFailureKind").GetString());
            Assert.Equal("bootstrap timed out", snapshot.GetProperty("lastBootstrapBackfillFailureReason").GetString());
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
            Assert.False(root.TryGetProperty("summary", out _));
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
