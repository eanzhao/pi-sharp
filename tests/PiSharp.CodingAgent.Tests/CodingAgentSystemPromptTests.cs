using PiSharp.Agent;

namespace PiSharp.CodingAgent.Tests;

public sealed class CodingAgentSystemPromptTests
{
    [Fact]
    public void Build_IncludesSelectedToolsContextFilesAndMetadata()
    {
        var prompt = CodingAgentSystemPrompt.Build(
            new BuildSystemPromptOptions
            {
                WorkingDirectory = "/repo",
                CurrentTime = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero),
                SelectedTools =
                [
                    BuiltInToolNames.Read,
                    BuiltInToolNames.Grep,
                ],
                PromptGuidelines =
                [
                    "Follow AGENTS.md first.",
                ],
                ContextFiles =
                [
                    new CodingAgentContextFile("AGENTS.md", "Use rg before grep."),
                ],
            });

        Assert.Contains("- read: Read file contents from the working directory", prompt);
        Assert.Contains("- grep: Search file contents recursively", prompt);
        Assert.Contains("Follow AGENTS.md first.", prompt);
        Assert.Contains("## AGENTS.md", prompt);
        Assert.Contains("Current date: 2026-04-15", prompt);
        Assert.Contains("Current working directory: /repo", prompt);
    }

    [Fact]
    public void Build_WithCustomPrompt_AppendsContextInsteadOfDefaultTemplate()
    {
        var prompt = CodingAgentSystemPrompt.Build(
            new BuildSystemPromptOptions
            {
                CustomPrompt = "You are custom.",
                AppendSystemPrompt = "Extra rule.",
                WorkingDirectory = "/repo",
                CurrentTime = new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero),
                ContextFiles =
                [
                    new CodingAgentContextFile("AGENTS.md", "Keep patches small."),
                ],
            });

        Assert.StartsWith("You are custom.", prompt);
        Assert.Contains("Extra rule.", prompt);
        Assert.Contains("# Project Context", prompt);
        Assert.DoesNotContain("Available tools:", prompt);
    }
}
