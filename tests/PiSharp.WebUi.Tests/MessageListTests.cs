using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.WebUi.Tests.Support;

namespace PiSharp.WebUi.Tests;

public sealed class MessageListTests
{
    [Fact]
    public async Task RenderAsync_RendersTranscriptAndStreamingAssistantState()
    {
        var user = new ChatMessage(ChatRole.User, "Summarize the diff.");
        var toolCall = new FunctionCallContent(
            "call-1",
            "bash",
            new Dictionary<string, object?>
            {
                ["command"] = "git diff",
            });

        var usage = new ExtendedUsageDetails
        {
            InputTokenCount = 1_200,
            OutputTokenCount = 240,
            Cost = new UsageCostBreakdown(0.001m, 0.002m, 0m, 0m),
        };

        var assistant = AgentMessageMetadata.WithAssistantMetadata(
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new TextReasoningContent("Need to inspect the repository first."),
                    new TextContent("Running `git diff`."),
                    toolCall,
                ]),
            TestFixtures.TestModel,
            finishReason: ChatFinishReason.ToolCalls,
            usage: usage);

        var toolResult = AgentMessageMetadata.CreateToolResultMessage(
            toolCall,
            AgentToolResult.FromText("M src/App.cs"),
            isError: false);

        var streaming = AgentMessageMetadata.CreateAssistantMessage(
            TestFixtures.TestModel,
            [new TextContent("partial reply")]);

        var html = await ComponentRenderer.RenderAsync<MessageList>(
            new Dictionary<string, object?>
            {
                ["Messages"] = new[] { user, assistant, toolResult },
                ["Tools"] = new[]
                {
                    AgentTool.Create(
                        (string command) => command,
                        name: "bash",
                        label: "Shell"),
                },
                ["PendingToolCalls"] = new HashSet<string>(StringComparer.Ordinal),
                ["StreamingMessage"] = streaming,
                ["IsStreaming"] = true,
            });

        Assert.Contains("Summarize the diff.", html);
        Assert.Contains("Running", html);
        Assert.Contains("Thinking", html);
        Assert.Contains("Shell", html);
        Assert.Contains("git diff", html);
        Assert.Contains("&#x2191;1.2k &#x2193;240 $0.0030", html);
        Assert.Contains("M src/App.cs", html);
        Assert.Contains("partial reply", html);
    }
}
