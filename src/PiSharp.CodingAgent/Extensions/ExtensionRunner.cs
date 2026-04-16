using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent;

public sealed class ExtensionRunner
{
    private readonly IReadOnlyList<ICodingAgentExtension> _extensions;
    private readonly Dictionary<string, AgentTool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionCommand> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionShortcut> _shortcuts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionFlag> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _pendingMessages = [];

    private readonly IExtensionApi _api;
    private CodingAgentSessionBuilder? _loadBuilder;
    private CodingAgentSession? _session;

    public ExtensionRunner(
        IEnumerable<ICodingAgentExtension> extensions,
        ModelMetadata model,
        ThinkingLevel thinkingLevel)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(model);

        _extensions = extensions.ToArray();
        CurrentModel = model;
        CurrentThinkingLevel = thinkingLevel;
        _api = new RunnerExtensionApi(this);
    }

    public IReadOnlyList<ICodingAgentExtension> Extensions => _extensions;

    public IExtensionApi Api => _api;

    public ModelMetadata CurrentModel { get; private set; }

    public ThinkingLevel CurrentThinkingLevel { get; private set; }

    public IReadOnlyDictionary<string, AgentTool> Tools => _tools;

    public IReadOnlyDictionary<string, ExtensionCommand> Commands => _commands;

    public IReadOnlyDictionary<string, ExtensionShortcut> Shortcuts => _shortcuts;

    public IReadOnlyDictionary<string, ExtensionFlag> Flags => _flags;

    public async Task LoadAsync(CodingAgentSessionBuilder builder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        _loadBuilder = builder;
        try
        {
            foreach (var extension in _extensions)
            {
                await extension.ConfigureSessionAsync(builder, _api, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _loadBuilder = null;
        }
    }

    public async Task BindAsync(CodingAgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        session.Agent.State.Model = CurrentModel;
        session.Agent.State.ThinkingLevel = CurrentThinkingLevel;

        if (_pendingMessages.Count == 0)
        {
            return;
        }

        foreach (var message in _pendingMessages.ToArray())
        {
            await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }

        _pendingMessages.Clear();
    }

    public async ValueTask DispatchAsync(
        CodingAgentSession session,
        AgentEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(@event);

        CurrentModel = session.State.Model;
        CurrentThinkingLevel = session.State.ThinkingLevel;

        foreach (var extension in _extensions)
        {
            await extension.OnAgentEventAsync(session, @event, cancellationToken).ConfigureAwait(false);
        }
    }

    private void RegisterTool(AgentTool tool, string? promptSnippet)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _tools[tool.Name] = tool;
        _loadBuilder?.AddTool(tool, promptSnippet);

        if (_session is null)
        {
            return;
        }

        var mergedTools = _session.Agent.State.Tools
            .Where(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal))
            .Concat([tool])
            .ToArray();

        _session.Agent.State.Tools = mergedTools;
    }

    private void RegisterCommand(ExtensionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _commands[command.Name] = command;
    }

    private void RegisterShortcut(ExtensionShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        _shortcuts[shortcut.Key] = shortcut;
    }

    private void RegisterFlag(ExtensionFlag flag)
    {
        ArgumentNullException.ThrowIfNull(flag);
        _flags[flag.Name] = flag;
    }

    private async ValueTask SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (_session is null)
        {
            _pendingMessages.Add(text);
            return;
        }

        var message = new ChatMessage(ChatRole.User, text);
        if (_session.State.IsStreaming)
        {
            _session.FollowUp(message);
            return;
        }

        await _session.PromptAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private void SetModel(ModelMetadata model)
    {
        ArgumentNullException.ThrowIfNull(model);
        CurrentModel = model;

        if (_session is not null)
        {
            _session.Agent.State.Model = model;
        }
    }

    private void SetThinkingLevel(ThinkingLevel thinkingLevel)
    {
        CurrentThinkingLevel = thinkingLevel;

        if (_session is not null)
        {
            _session.Agent.State.ThinkingLevel = thinkingLevel;
        }
    }

    private sealed class RunnerExtensionApi(ExtensionRunner runner) : IExtensionApi
    {
        public void RegisterTool(AgentTool tool, string? promptSnippet = null) =>
            runner.RegisterTool(tool, promptSnippet);

        public void RegisterCommand(ExtensionCommand command) =>
            runner.RegisterCommand(command);

        public void RegisterShortcut(ExtensionShortcut shortcut) =>
            runner.RegisterShortcut(shortcut);

        public void RegisterFlag(ExtensionFlag flag) =>
            runner.RegisterFlag(flag);

        public ValueTask SendMessage(string text, CancellationToken cancellationToken = default) =>
            runner.SendMessageAsync(text, cancellationToken);

        public void SetModel(ModelMetadata model) =>
            runner.SetModel(model);

        public ThinkingLevel GetThinkingLevel() =>
            runner.CurrentThinkingLevel;

        public void SetThinkingLevel(ThinkingLevel thinkingLevel) =>
            runner.SetThinkingLevel(thinkingLevel);
    }
}
