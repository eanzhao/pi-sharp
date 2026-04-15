using Microsoft.Extensions.AI;

namespace PiSharp.Agent;

public sealed record AgentContext(
    string SystemPrompt,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AgentTool>? Tools = null);

public sealed record BeforeToolCallDecision(
    bool Block = false,
    string? Reason = null);

public sealed class AfterToolCallOverride
{
    public Optional<IReadOnlyList<AIContent>> Content { get; init; } = Optional<IReadOnlyList<AIContent>>.Unset;

    public Optional<object?> Value { get; init; } = Optional<object?>.Unset;

    public Optional<object?> Details { get; init; } = Optional<object?>.Unset;

    public bool? IsError { get; init; }
}

public sealed record BeforeToolCallContext(
    ChatMessage AssistantMessage,
    FunctionCallContent ToolCall,
    AIFunctionArguments Arguments,
    AgentContext Context);

public sealed record AfterToolCallContext(
    ChatMessage AssistantMessage,
    FunctionCallContent ToolCall,
    AIFunctionArguments Arguments,
    AgentToolResult Result,
    bool IsError,
    AgentContext Context);

public delegate ValueTask<IReadOnlyList<ChatMessage>> AgentMessageTransform(
    IReadOnlyList<ChatMessage> messages,
    CancellationToken cancellationToken);

public delegate ValueTask<IReadOnlyList<ChatMessage>> PendingMessagesProvider(
    CancellationToken cancellationToken);

public delegate ValueTask<BeforeToolCallDecision?> BeforeToolCallCallback(
    BeforeToolCallContext context,
    CancellationToken cancellationToken);

public delegate ValueTask<AfterToolCallOverride?> AfterToolCallCallback(
    AfterToolCallContext context,
    CancellationToken cancellationToken);

public delegate ValueTask AgentEventHandler(
    AgentEvent @event,
    CancellationToken cancellationToken);
