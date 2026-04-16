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
}
