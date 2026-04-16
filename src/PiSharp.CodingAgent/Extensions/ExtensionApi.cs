using Microsoft.Extensions.AI;
using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public sealed record ExtensionCommand(string Name, string Description, Func<IExtensionContext, Task> Handler);

public sealed record ExtensionShortcut(string Key, string Description, Func<IExtensionContext, Task> Handler);

public sealed record ExtensionFlag(string Name, string Description, object? DefaultValue = null);

public interface IExtensionApi
{
    void RegisterTool(AgentTool tool, string? promptSnippet = null);
    void RegisterCommand(ExtensionCommand command);
    void RegisterShortcut(ExtensionShortcut shortcut);
    void RegisterFlag(ExtensionFlag flag);
    object? GetFlag(string name);
    void SendUserMessage(string text);
    string? GetSessionName();
    void SetSessionName(string name);
    ThinkingLevel GetThinkingLevel();
    void SetThinkingLevel(ThinkingLevel level);
    IReadOnlyList<string> GetActiveToolNames();
}

public interface IExtensionContext
{
    string WorkingDirectory { get; }
    bool IsIdle { get; }
    string? SystemPrompt { get; }
    IReadOnlyList<ChatMessage> Messages { get; }
}

public sealed class ExtensionRunner
{
    private readonly List<ICodingAgentExtension> _extensions = [];
    private readonly Dictionary<string, ExtensionCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtensionShortcut> _shortcuts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionFlag> _flags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _flagValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentTool> _registeredTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolSnippets = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ExtensionCommand> Commands => _commands;
    public IReadOnlyDictionary<string, ExtensionShortcut> Shortcuts => _shortcuts;
    public IReadOnlyDictionary<string, ExtensionFlag> Flags => _flags;
    public IReadOnlyDictionary<string, AgentTool> RegisteredTools => _registeredTools;
    public IReadOnlyDictionary<string, string> ToolSnippets => _toolSnippets;

    public void AddExtension(ICodingAgentExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        _extensions.Add(extension);
    }

    public IExtensionApi CreateApi() => new RunnerExtensionApi(this);

    public void RegisterTool(AgentTool tool, string? snippet)
    {
        _registeredTools[tool.Name] = tool;
        if (!string.IsNullOrWhiteSpace(snippet))
        {
            _toolSnippets[tool.Name] = snippet;
        }
    }

    public void RegisterCommand(ExtensionCommand command) => _commands[command.Name] = command;
    public void RegisterShortcut(ExtensionShortcut shortcut) => _shortcuts[shortcut.Key] = shortcut;

    public void RegisterFlag(ExtensionFlag flag)
    {
        _flags[flag.Name] = flag;
        _flagValues.TryAdd(flag.Name, flag.DefaultValue);
    }

    public object? GetFlag(string name) =>
        _flagValues.TryGetValue(name, out var value) ? value : null;

    public async Task EmitEventAsync(CodingAgentSession session, AgentEvent @event, CancellationToken ct = default)
    {
        foreach (var extension in _extensions)
        {
            await extension.OnAgentEventAsync(session, @event, ct).ConfigureAwait(false);
        }
    }

    private sealed class RunnerExtensionApi(ExtensionRunner runner) : IExtensionApi
    {
        private ThinkingLevel _thinkingLevel = ThinkingLevel.Off;
        private string? _sessionName;
        private readonly List<string> _activeToolNames = [];
        private readonly List<string> _pendingUserMessages = [];

        public void RegisterTool(AgentTool tool, string? promptSnippet = null) => runner.RegisterTool(tool, promptSnippet);
        public void RegisterCommand(ExtensionCommand command) => runner.RegisterCommand(command);
        public void RegisterShortcut(ExtensionShortcut shortcut) => runner.RegisterShortcut(shortcut);
        public void RegisterFlag(ExtensionFlag flag) => runner.RegisterFlag(flag);
        public object? GetFlag(string name) => runner.GetFlag(name);
        public void SendUserMessage(string text) => _pendingUserMessages.Add(text);
        public string? GetSessionName() => _sessionName;
        public void SetSessionName(string name) => _sessionName = name;
        public ThinkingLevel GetThinkingLevel() => _thinkingLevel;
        public void SetThinkingLevel(ThinkingLevel level) => _thinkingLevel = level;
        public IReadOnlyList<string> GetActiveToolNames() => _activeToolNames;
    }
}
