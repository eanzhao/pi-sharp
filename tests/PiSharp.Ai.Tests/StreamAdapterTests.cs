using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Ai.Tests;

public sealed class StreamAdapterTests
{
    [Fact]
    public async Task ToEventsAsync_EmitsTextLifecycleAndDone()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(new TextContent("Hel")),
            CreateUpdate(
                [
                    new TextContent("lo"),
                    new UsageContent(new UsageDetails
                    {
                        InputTokenCount = 12,
                        OutputTokenCount = 7,
                        CachedInputTokenCount = 3,
                    }),
                ],
                ChatFinishReason.Stop));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event => Assert.Equal("Hel", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.Equal("lo", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.TextEnd>(@event),
            @event =>
            {
                var done = Assert.IsType<AssistantMessageEvent.Done>(@event);
                Assert.Equal(ChatFinishReason.Stop, done.FinishReason);
                Assert.NotNull(done.Usage);
                Assert.Equal(12, done.Usage!.InputTokenCount);
                Assert.Equal(7, done.Usage.OutputTokenCount);
                Assert.Equal(3, done.Usage.CacheReadTokenCount);
            });
    }

    [Fact]
    public async Task ToEventsAsync_EmitsThinkingTextAndToolCallTransitions()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(new TextReasoningContent("Plan")),
            CreateUpdate(new TextContent("Answer: ")),
            CreateUpdate(
                new FunctionCallContent(
                    "call-1",
                    "read_file",
                    new Dictionary<string, object?>
                    {
                        ["path"] = "README.md",
                    })),
            CreateUpdate(
                new FunctionCallContent(
                    "call-1",
                    "read_file",
                    new Dictionary<string, object?>
                    {
                        ["path"] = "README.md",
                        ["offset"] = 0L,
                    }),
                finishReason: ChatFinishReason.ToolCalls));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.ThinkingStart>(@event),
            @event => Assert.Equal("Plan", Assert.IsType<AssistantMessageEvent.ThinkingDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.ThinkingEnd>(@event),
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event => Assert.Equal("Answer: ", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.TextEnd>(@event),
            @event =>
            {
                var toolCallStart = Assert.IsType<AssistantMessageEvent.ToolCallStart>(@event);
                Assert.Equal("call-1", toolCallStart.CallId);
                Assert.Equal("read_file", toolCallStart.Name);
            },
            @event =>
            {
                var toolCallDelta = Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event);
                Assert.Equal("call-1", toolCallDelta.CallId);
                Assert.Equal("{\"path\":\"README.md\"}", toolCallDelta.ArgumentsDelta);
            },
            @event =>
            {
                var toolCallDelta = Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event);
                Assert.Equal("call-1", toolCallDelta.CallId);
                Assert.Equal(",\"offset\":0}", toolCallDelta.ArgumentsDelta);
            },
            @event =>
            {
                var toolCallEnd = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(@event);
                Assert.Equal("call-1", toolCallEnd.CallId);
            },
            @event =>
            {
                var done = Assert.IsType<AssistantMessageEvent.Done>(@event);
                Assert.Equal(ChatFinishReason.ToolCalls, done.FinishReason);
            });
    }

    [Fact]
    public async Task ToEventsAsync_EmitsErrorWhenUnderlyingStreamFails()
    {
        var events = await ReadAllAsync(StreamAdapter.ToEvents(FailingAsyncEnumerable()));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event => Assert.Equal("partial", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.TextEnd>(@event),
            @event =>
            {
                var error = Assert.IsType<AssistantMessageEvent.Error>(@event);
                Assert.Equal("boom", error.Exception.Message);
            });
    }

    [Fact]
    public async Task ToEventsAsync_EmitsDoneOnly_WhenStreamIsEmpty()
    {
        var updates = CreateAsyncEnumerable();

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        var done = Assert.IsType<AssistantMessageEvent.Done>(Assert.Single(events));
        Assert.Null(done.FinishReason);
        Assert.Null(done.Usage);
    }

    [Fact]
    public async Task ToEventsAsync_EmitsDoneWithNullUsage_WhenNoUsageContentPresent()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(new TextContent("hello"), ChatFinishReason.Stop));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event => Assert.Equal("hello", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.TextEnd>(@event),
            @event =>
            {
                var done = Assert.IsType<AssistantMessageEvent.Done>(@event);
                Assert.Equal(ChatFinishReason.Stop, done.FinishReason);
                Assert.Null(done.Usage);
            });
    }

    [Fact]
    public async Task ToEventsAsync_HandlesMultipleToolCallsWithDistinctCallIds()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(
                new FunctionCallContent(
                    "call-1",
                    "read_file",
                    new Dictionary<string, object?> { ["path"] = "a.txt" })),
            CreateUpdate(
                new FunctionCallContent(
                    "call-2",
                    "write_file",
                    new Dictionary<string, object?> { ["path"] = "b.txt" }),
                finishReason: ChatFinishReason.ToolCalls));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        Assert.Collection(
            events,
            @event =>
            {
                var start = Assert.IsType<AssistantMessageEvent.ToolCallStart>(@event);
                Assert.Equal("call-1", start.CallId);
                Assert.Equal("read_file", start.Name);
            },
            @event => Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event),
            @event =>
            {
                var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(@event);
                Assert.Equal("call-1", end.CallId);
            },
            @event =>
            {
                var start = Assert.IsType<AssistantMessageEvent.ToolCallStart>(@event);
                Assert.Equal("call-2", start.CallId);
                Assert.Equal("write_file", start.Name);
            },
            @event => Assert.IsType<AssistantMessageEvent.ToolCallDelta>(@event),
            @event =>
            {
                var end = Assert.IsType<AssistantMessageEvent.ToolCallEnd>(@event);
                Assert.Equal("call-2", end.CallId);
            },
            @event => Assert.IsType<AssistantMessageEvent.Done>(@event));
    }

    [Fact]
    public async Task ToEventsAsync_SkipsEmptyTextContent()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(new TextContent("")),
            CreateUpdate(new TextContent("real")),
            CreateUpdate(new TextContent(""), ChatFinishReason.Stop));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        Assert.Collection(
            events,
            @event => Assert.IsType<AssistantMessageEvent.TextStart>(@event),
            @event => Assert.Equal("real", Assert.IsType<AssistantMessageEvent.TextDelta>(@event).Text),
            @event => Assert.IsType<AssistantMessageEvent.TextEnd>(@event),
            @event => Assert.IsType<AssistantMessageEvent.Done>(@event));
    }

    [Fact]
    public async Task ToEventsAsync_AccumulatesUsageAcrossMultipleUpdates()
    {
        var updates = CreateAsyncEnumerable(
            CreateUpdate(
                new UsageContent(new UsageDetails { InputTokenCount = 10 })),
            CreateUpdate(
                [new UsageContent(new UsageDetails { OutputTokenCount = 5 })],
                ChatFinishReason.Stop));

        var events = await ReadAllAsync(StreamAdapter.ToEvents(updates));

        var done = Assert.IsType<AssistantMessageEvent.Done>(Assert.Single(events));
        Assert.NotNull(done.Usage);
        Assert.Equal(10, done.Usage!.InputTokenCount);
        Assert.Equal(5, done.Usage.OutputTokenCount);
    }

    private static ChatResponseUpdate CreateUpdate(
        params AIContent[] contents) =>
        CreateUpdate(contents.AsEnumerable(), null);

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

    private static async IAsyncEnumerable<ChatResponseUpdate> CreateAsyncEnumerable(params ChatResponseUpdate[] updates)
    {
        foreach (var update in updates)
        {
            yield return update;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> FailingAsyncEnumerable()
    {
        yield return CreateUpdate(new TextContent("partial"));
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }

    private static async Task<List<AssistantMessageEvent>> ReadAllAsync(IAsyncEnumerable<AssistantMessageEvent> events)
    {
        var collectedEvents = new List<AssistantMessageEvent>();

        await foreach (var @event in events)
        {
            collectedEvents.Add(@event);
        }

        return collectedEvents;
    }
}
