using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent.Tests.Support;
using PiSharp.Ai;

namespace PiSharp.CodingAgent.Tests;

public sealed class CodingAgentSessionTests
{
    private static readonly ModelMetadata TestModel = new(
        "gpt-4.1-mini",
        "GPT-4.1 mini",
        ApiId.OpenAi,
        ProviderId.OpenAi,
        1_000_000,
        32_768,
        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
        ModelPricing.Free);

    [Fact]
    public async Task CreateAsync_AppliesExtensionPromptAndCustomTool()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(
                    new FunctionCallContent(
                        "call-1",
                        "hello",
                        new Dictionary<string, object?>
                        {
                            ["name"] = "pi-sharp",
                        }),
                    ChatFinishReason.ToolCalls),
            ],
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var extension = new TestExtension();
        using var session = await CodingAgentSession.CreateAsync(
            client,
            new CodingAgentSessionOptions
            {
                Model = TestModel,
                WorkingDirectory = Path.GetTempPath(),
                Extensions = [extension],
            });

        await session.PromptAsync("Say hi");

        Assert.Contains("Extension guideline.", session.SystemPrompt);
        Assert.Contains("Custom hello tool", session.SystemPrompt);
        Assert.Contains(extension.SeenEvents, @event => @event is AgentEvent.ToolExecutionCompleted { ToolName: "hello" });

        var toolMessage = Assert.Single(session.State.Messages, message => message.Role == ChatRole.Tool);
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Equal("hello pi-sharp", NormalizeScalarResult(result.Result));
    }

    [Fact]
    public async Task CreateAsync_UsesDefaultBuiltInToolsWhenNoSelectionProvided()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ready"), ChatFinishReason.Stop),
            ]);

        using var session = await CodingAgentSession.CreateAsync(
            client,
            new CodingAgentSessionOptions
            {
                Model = TestModel,
                WorkingDirectory = Path.GetTempPath(),
            });

        await session.PromptAsync("status");

        Assert.Equal(
            [
                BuiltInToolNames.Read,
                BuiltInToolNames.Bash,
                BuiltInToolNames.Edit,
                BuiltInToolNames.Write,
            ],
            session.ActiveToolNames);

        Assert.NotNull(client.Options[0].Tools);
        Assert.Equal(4, client.Options[0].Tools!.Count);
    }

    private static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        CreateUpdate([content], finishReason);

    private static ChatResponseUpdate CreateUpdate(
        IEnumerable<AIContent> contents,
        ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, contents.ToList())
        {
            FinishReason = finishReason,
        };

    private static object? NormalizeScalarResult(object? value) =>
        value is System.Text.Json.JsonElement jsonElement && jsonElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? jsonElement.GetString()
            : value;

    private sealed class TestExtension : ICodingAgentExtension
    {
        public List<AgentEvent> SeenEvents { get; } = [];

        public ValueTask ConfigureSessionAsync(CodingAgentSessionBuilder builder, CancellationToken cancellationToken = default)
        {
            builder.AddPromptGuideline("Extension guideline.");
            builder.AddTool(
                AgentTool.Create((string name) => $"hello {name}", name: "hello"),
                promptSnippet: "Custom hello tool");
            return ValueTask.CompletedTask;
        }

        public ValueTask OnAgentEventAsync(CodingAgentSession session, AgentEvent @event, CancellationToken cancellationToken = default)
        {
            SeenEvents.Add(@event);
            return ValueTask.CompletedTask;
        }
    }
}
