using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public sealed class AgentLoopOptions
{
    public required IChatClient ChatClient { get; init; }

    public required ModelMetadata Model { get; init; }

    public ChatOptions? ChatOptions { get; init; }

    public AgentMessageTransform? ConvertToLlm { get; init; }

    public AgentMessageTransform? TransformContext { get; init; }

    public PendingMessagesProvider? GetSteeringMessages { get; init; }

    public PendingMessagesProvider? GetFollowUpMessages { get; init; }

    public BeforeToolCallCallback? BeforeToolCall { get; init; }

    public AfterToolCallCallback? AfterToolCall { get; init; }

    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;

    public ThinkingLevel ThinkingLevel { get; init; } = ThinkingLevel.Off;
}
