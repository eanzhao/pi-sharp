using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class MomSystemPromptTests
{
    [Fact]
    public void Build_IncludesSlackUserAndChannelMappings()
    {
        var prompt = MomSystemPrompt.Build(
            new MomSystemPromptOptions
            {
                WorkspaceDirectory = "/workspace",
                ChannelId = "C123",
                ChannelName = "general",
                ChannelDirectory = "/workspace/C123",
                Memory = "(empty)",
                Users =
                [
                    new SlackUserInfo("U123", "alice", "Alice Example"),
                ],
                Channels =
                [
                    new SlackChannelInfo("C123", "general"),
                    new SlackChannelInfo("D123", "DM:alice"),
                ],
                CurrentTime = new DateTimeOffset(2026, 4, 16, 9, 30, 0, TimeSpan.FromHours(8)),
            });

        Assert.Contains("Current channel: C123 (general)", prompt);
        Assert.Contains("C123\t#general", prompt);
        Assert.Contains("D123\tDM:alice", prompt);
        Assert.Contains("U123\t@alice\tAlice Example", prompt);
    }
}
