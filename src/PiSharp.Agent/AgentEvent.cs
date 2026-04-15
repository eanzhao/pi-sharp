using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public abstract record AgentEvent
{
    private AgentEvent()
    {
    }

    public sealed record AgentStarted : AgentEvent;

    public sealed record AgentCompleted(IReadOnlyList<ChatMessage> Messages) : AgentEvent;

    public sealed record TurnStarted : AgentEvent;

    public sealed record TurnCompleted(ChatMessage Message, IReadOnlyList<ChatMessage> ToolResults) : AgentEvent;

    public sealed record MessageStarted(ChatMessage Message) : AgentEvent;

    public sealed record MessageUpdated(ChatMessage Message, AssistantMessageEvent AssistantMessageEvent) : AgentEvent;

    public sealed record MessageCompleted(ChatMessage Message) : AgentEvent;

    public sealed record ToolExecutionStarted(
        string ToolCallId,
        string ToolName,
        AIFunctionArguments Arguments) : AgentEvent;

    public sealed record ToolExecutionUpdated(
        string ToolCallId,
        string ToolName,
        AIFunctionArguments Arguments,
        AgentToolResult PartialResult) : AgentEvent;

    public sealed record ToolExecutionCompleted(
        string ToolCallId,
        string ToolName,
        AgentToolResult Result,
        bool IsError) : AgentEvent;
}
