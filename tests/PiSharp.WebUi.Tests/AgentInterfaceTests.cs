using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.WebUi.Tests.Support;
using AgentRuntime = PiSharp.Agent.Agent;

namespace PiSharp.WebUi.Tests;

public sealed class AgentInterfaceTests
{
    [Fact]
    public async Task RenderAsync_ShowsBoundModelTranscriptAndComposer()
    {
        var agent = new AgentRuntime(
            new FakeChatClient(
                [
                    TestFixtures.CreateUpdate(new TextContent("noop"), ChatFinishReason.Stop),
                ]),
            new AgentOptions
            {
                Model = TestFixtures.TestModel,
            });

        agent.State.Tools = [
            AgentTool.Create(
                (string query) => query,
                name: "search",
                label: "Repo Search"),
        ];
        agent.State.Messages = [
            new ChatMessage(ChatRole.User, "Ping"),
        ];

        var html = await ComponentRenderer.RenderAsync<AgentInterface>(
            new Dictionary<string, object?>
            {
                ["Agent"] = agent,
                ["Placeholder"] = "Type here",
            });

        Assert.Contains("GPT-4.1 mini", html);
        Assert.Contains("openai/gpt-4.1-mini", html);
        Assert.Contains("idle", html);
        Assert.Contains("1 tool(s)", html);
        Assert.Contains("Ping", html);
        Assert.Contains("Type here", html);
        Assert.Contains("Send", html);
    }
}
