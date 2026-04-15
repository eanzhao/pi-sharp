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
        Assert.Equal(["README.md"], arguments.FileArguments);
        Assert.Equal(["fix", "tests"], arguments.Messages);
        Assert.Empty(arguments.Diagnostics);
    }
}
