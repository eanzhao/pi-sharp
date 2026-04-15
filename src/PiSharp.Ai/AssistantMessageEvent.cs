using Microsoft.Extensions.AI;

namespace PiSharp.Ai;

public abstract record AssistantMessageEvent
{
    private AssistantMessageEvent()
    {
    }

    public sealed record TextStart : AssistantMessageEvent;

    public sealed record TextDelta(string Text) : AssistantMessageEvent;

    public sealed record TextEnd : AssistantMessageEvent;

    public sealed record ThinkingStart : AssistantMessageEvent;

    public sealed record ThinkingDelta(string Text) : AssistantMessageEvent;

    public sealed record ThinkingEnd : AssistantMessageEvent;

    public sealed record ToolCallStart(string CallId, string Name) : AssistantMessageEvent;

    public sealed record ToolCallDelta(string CallId, string ArgumentsDelta) : AssistantMessageEvent;

    public sealed record ToolCallEnd(string CallId) : AssistantMessageEvent;

    public sealed record Done(ChatFinishReason? FinishReason, ExtendedUsageDetails? Usage) : AssistantMessageEvent;

    public sealed record Error(Exception Exception) : AssistantMessageEvent;
}
