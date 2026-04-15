using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public sealed class AgentState
{
    private IReadOnlyList<AgentTool> _tools = Array.Empty<AgentTool>();
    private IReadOnlyList<ChatMessage> _messages = Array.Empty<ChatMessage>();

    public string SystemPrompt { get; set; } = string.Empty;

    public ModelMetadata Model { get; set; } = AgentDefaults.UnknownModel;

    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Off;

    public IReadOnlyList<AgentTool> Tools
    {
        get => _tools;
        set => _tools = value?.ToArray() ?? Array.Empty<AgentTool>();
    }

    public IReadOnlyList<ChatMessage> Messages
    {
        get => _messages;
        set => _messages = value?.ToArray() ?? Array.Empty<ChatMessage>();
    }

    public bool IsStreaming { get; internal set; }

    public ChatMessage? StreamingMessage { get; internal set; }

    public IReadOnlySet<string> PendingToolCalls { get; internal set; } = new HashSet<string>(StringComparer.Ordinal);

    public string? ErrorMessage { get; internal set; }
}
