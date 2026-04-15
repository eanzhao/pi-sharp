using PiSharp.Agent;

namespace PiSharp.Cli.Tests;

public sealed class CliArgumentsParserTests
{
    [Fact]
    public void Parse_ReadsCoreFlagsAndMessageArguments()
    {
        var arguments = CliArgumentsParser.Parse(
            [
                "--provider",
                "openai",
                "--model",
                "gpt-4.1-mini",
                "--tools",
                "read,grep,find",
                "--thinking",
                "high",
                "--session-dir",
                ".pi-sharp/sessions",
                "--resume",
                "latest",
                "@README.md",
                "fix",
                "tests",
            ]);

        Assert.Equal("openai", arguments.Provider);
        Assert.Equal("gpt-4.1-mini", arguments.Model);
        Assert.Equal(
            [
                "read",
                "grep",
                "find",
            ],
            arguments.Tools);
        Assert.Equal(ThinkingLevel.High, arguments.ThinkingLevel);
        Assert.Equal(".pi-sharp/sessions", arguments.SessionDirectory);
        Assert.Equal("latest", arguments.ResumeSession);
        Assert.Equal(["README.md"], arguments.FileArguments);
        Assert.Equal(["fix", "tests"], arguments.Messages);
        Assert.Empty(arguments.Diagnostics);
    }

    [Fact]
    public void Parse_RejectsConflictingSessionFlags()
    {
        var arguments = CliArgumentsParser.Parse(
            [
                "--resume",
                "session-a",
                "--fork",
                "session-b",
            ]);

        Assert.Contains(
            arguments.Diagnostics,
            diagnostic => diagnostic.Severity == CliDiagnosticSeverity.Error &&
                diagnostic.Message.Contains("--resume cannot be combined with --fork.", StringComparison.Ordinal));
    }
}
