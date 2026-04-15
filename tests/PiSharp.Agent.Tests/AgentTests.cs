using Microsoft.Extensions.AI;
using PiSharp.Agent.Tests.Support;
using PiSharp.Ai;
using System.Text.Json;

namespace PiSharp.Agent.Tests;

public sealed class AgentTests
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
    public async Task PromptAsync_ExecutesToolCallAndCompletesTranscript()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(new TextContent("Checking ")),
                CreateUpdate(
                    new FunctionCallContent(
                        "call-1",
                        "echo",
                        new Dictionary<string, object?>
                        {
                            ["input"] = "hello",
                        }),
                    ChatFinishReason.ToolCalls),
            ],
            [
                CreateUpdate(new TextContent("Done"), ChatFinishReason.Stop),
            ]);

        var events = new List<AgentEvent>();
        var agent = new Agent(
            client,
            new AgentOptions
            {
                Model = TestModel,
                SystemPrompt = "You are helpful.",
                Tools =
                [
                    AgentTool.Create(
                        (string input) => $"echo:{input}",
                        name: "echo",
                        label: "Echo"),
                ],
            });

        agent.Subscribe((@event, _) =>
        {
            events.Add(@event);
            return ValueTask.CompletedTask;
        });

        await agent.PromptAsync("Hi");

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal("You are helpful.", client.Options[0].Instructions);
        Assert.NotNull(client.Options[0].Tools);
        Assert.Single(client.Options[0].Tools!);

        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal(ChatRole.User, message.Role),
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Equal("Checking ", message.Text);
                Assert.True(AgentMessageMetadata.TryGetFinishReason(message, out var reason));
                Assert.Equal(ChatFinishReason.ToolCalls, reason);
            },
            message =>
            {
                Assert.Equal(ChatRole.Tool, message.Role);
                Assert.True(AgentMessageMetadata.TryGetToolName(message, out var toolName));
                Assert.Equal("echo", toolName);
                Assert.False(AgentMessageMetadata.IsToolError(message));
                var result = Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents));
                Assert.Equal("echo:hello", NormalizeScalarResult(result.Result));
            },
            message =>
            {
                Assert.Equal(ChatRole.Assistant, message.Role);
                Assert.Equal("Done", message.Text);
                Assert.True(AgentMessageMetadata.TryGetFinishReason(message, out var reason));
                Assert.Equal(ChatFinishReason.Stop, reason);
            });

        Assert.Contains(events, @event => @event is AgentEvent.ToolExecutionStarted { ToolName: "echo" });
        Assert.Contains(events, @event => @event is AgentEvent.ToolExecutionCompleted { ToolName: "echo", IsError: false });
    }

    [Fact]
    public async Task BeforeToolCall_CanBlockExecution()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(
                    new FunctionCallContent(
                        "call-1",
                        "echo",
                        new Dictionary<string, object?>
                        {
                            ["input"] = "hello",
                        }),
                    ChatFinishReason.ToolCalls),
            ],
            [
                CreateUpdate(new TextContent("blocked"), ChatFinishReason.Stop),
            ]);

        var agent = new Agent(
            client,
            new AgentOptions
            {
                Model = TestModel,
                Tools =
                [
                    AgentTool.Create((string input) => $"echo:{input}", name: "echo"),
                ],
                BeforeToolCall = (_, _) =>
                    ValueTask.FromResult<BeforeToolCallDecision?>(new BeforeToolCallDecision(true, "blocked by policy")),
            });

        await agent.PromptAsync("Hi");

        var toolMessage = Assert.Single(agent.State.Messages, message => message.Role == ChatRole.Tool);
        Assert.True(AgentMessageMetadata.IsToolError(toolMessage));
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Equal("blocked by policy", result.Result);
    }

    [Fact]
    public async Task AfterToolCall_CanOverrideResultAndErrorFlag()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(
                    new FunctionCallContent(
                        "call-1",
                        "echo",
                        new Dictionary<string, object?>
                        {
                            ["input"] = "hello",
                        }),
                    ChatFinishReason.ToolCalls),
            ],
            [
                CreateUpdate(new TextContent("patched"), ChatFinishReason.Stop),
            ]);

        var agent = new Agent(
            client,
            new AgentOptions
            {
                Model = TestModel,
                Tools =
                [
                    AgentTool.Create((string input) => $"echo:{input}", name: "echo"),
                ],
                AfterToolCall = (_, _) =>
                    ValueTask.FromResult<AfterToolCallOverride?>(new AfterToolCallOverride
                    {
                        Value = "patched-result",
                        Content = new Optional<IReadOnlyList<AIContent>>(
                            [new TextContent("patched-result")]),
                        IsError = true,
                    }),
            });

        await agent.PromptAsync("Hi");

        var toolMessage = Assert.Single(agent.State.Messages, message => message.Role == ChatRole.Tool);
        Assert.True(AgentMessageMetadata.IsToolError(toolMessage));
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(toolMessage.Contents));
        Assert.Equal("patched-result", result.Result);
    }

    [Fact]
    public async Task ParallelToolExecution_RunsConcurrentlyButCommitsInSourceOrder()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(
                    [
                        new FunctionCallContent(
                            "call-slow",
                            "slow",
                            new Dictionary<string, object?>
                            {
                                ["input"] = "slow",
                            }),
                        new FunctionCallContent(
                            "call-fast",
                            "fast",
                            new Dictionary<string, object?>
                            {
                                ["input"] = "fast",
                            }),
                    ],
                    ChatFinishReason.ToolCalls),
            ],
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastStartedBeforeSlowCompleted = false;

        var agent = new Agent(
            client,
            new AgentOptions
            {
                Model = TestModel,
                ToolExecution = ToolExecutionMode.Parallel,
                Tools =
                [
                    AgentTool.Create(
                        async Task<string> (string input, CancellationToken cancellationToken) =>
                        {
                            slowStarted.TrySetResult();
                            await releaseSlow.Task.WaitAsync(cancellationToken);
                            return input;
                        },
                        name: "slow"),
                    AgentTool.Create(
                        async Task<string> (string input) =>
                        {
                            await slowStarted.Task;
                            fastStartedBeforeSlowCompleted = !releaseSlow.Task.IsCompleted;
                            releaseSlow.TrySetResult();
                            return input;
                        },
                        name: "fast"),
                ],
            });

        await agent.PromptAsync("run both");

        Assert.True(fastStartedBeforeSlowCompleted);

        var toolMessages = agent.State.Messages.Where(message => message.Role == ChatRole.Tool).ToArray();
        Assert.Equal(2, toolMessages.Length);
        Assert.True(AgentMessageMetadata.TryGetToolName(toolMessages[0], out var firstToolName));
        Assert.True(AgentMessageMetadata.TryGetToolName(toolMessages[1], out var secondToolName));
        Assert.Equal("slow", firstToolName);
        Assert.Equal("fast", secondToolName);
    }

    [Fact]
    public async Task ContinueAsync_WithAssistantTailRunsQueuedSteeringMessage()
    {
        var client = new FakeChatClient(
            [
                CreateUpdate(new TextContent("continued"), ChatFinishReason.Stop),
            ]);

        var agent = new Agent(
            client,
            new AgentOptions
            {
                Model = TestModel,
                Messages =
                [
                    new ChatMessage(ChatRole.User, "hi"),
                    AgentMessageMetadata.WithAssistantMetadata(
                        new ChatMessage(ChatRole.Assistant, "done"),
                        TestModel,
                        ChatFinishReason.Stop),
                ],
            });

        agent.Steer(new ChatMessage(ChatRole.User, "next"));

        await agent.ContinueAsync();

        Assert.Collection(
            agent.State.Messages,
            message => Assert.Equal("hi", message.Text),
            message => Assert.Equal("done", message.Text),
            message => Assert.Equal("next", message.Text),
            message => Assert.Equal("continued", message.Text));
    }

    private static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        CreateUpdate([content], finishReason);

    private static ChatResponseUpdate CreateUpdate(
        IEnumerable<AIContent> contents,
        ChatFinishReason? finishReason = null)
    {
        var update = new ChatResponseUpdate(ChatRole.Assistant, contents.ToList())
        {
            FinishReason = finishReason,
        };

        return update;
    }

    private static object? NormalizeScalarResult(object? value) =>
        value switch
        {
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            _ => value,
        };
}
