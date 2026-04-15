using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using AgentRuntime = PiSharp.Agent.Agent;

namespace PiSharp.CodingAgent;

public sealed class CodingAgentSessionOptions
{
    public ModelMetadata Model { get; init; } = new(
        "unknown",
        "Unknown",
        new ApiId("unknown"),
        new ProviderId("unknown"),
        0,
        0,
        ModelCapability.None,
        ModelPricing.Free);

    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    public ThinkingLevel ThinkingLevel { get; init; } = ThinkingLevel.Off;

    public ChatOptions? ChatOptions { get; init; }

    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    public IReadOnlyList<string>? ActiveToolNames { get; init; }

    public IReadOnlyList<AgentTool>? AdditionalTools { get; init; }

    public IReadOnlyList<ICodingAgentExtension>? Extensions { get; init; }

    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public IReadOnlyList<CodingAgentContextFile>? ContextFiles { get; init; }

    public string? CustomSystemPrompt { get; init; }

    public string? AppendSystemPrompt { get; init; }

    public CodingAgentToolOptions? ToolOptions { get; init; }

    public AgentMessageTransform? ConvertToLlm { get; init; }

    public AgentMessageTransform? TransformContext { get; init; }

    public BeforeToolCallCallback? BeforeToolCall { get; init; }

    public AfterToolCallCallback? AfterToolCall { get; init; }

    public PendingMessageQueueMode SteeringMode { get; init; } = PendingMessageQueueMode.OneAtATime;

    public PendingMessageQueueMode FollowUpMode { get; init; } = PendingMessageQueueMode.OneAtATime;

    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
}

public sealed class CodingAgentSessionBuilder
{
    private readonly Dictionary<string, AgentTool> _additionalTools = new(StringComparer.Ordinal);

    public CodingAgentSessionBuilder(string workingDirectory, IEnumerable<string> activeToolNames)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory);
        ActiveToolNames = activeToolNames.ToList();
    }

    public string WorkingDirectory { get; }

    public IList<string> ActiveToolNames { get; }

    public IList<string> PromptGuidelines { get; } = [];

    public IList<CodingAgentContextFile> ContextFiles { get; } = [];

    public IDictionary<string, string> ToolSnippets { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string? CustomSystemPrompt { get; set; }

    public string? AppendSystemPrompt { get; set; }

    public IReadOnlyDictionary<string, AgentTool> AdditionalTools => _additionalTools;

    public void AddTool(AgentTool tool, string? promptSnippet = null)
    {
        ArgumentNullException.ThrowIfNull(tool);

        _additionalTools[tool.Name] = tool;
        if (!string.IsNullOrWhiteSpace(promptSnippet))
        {
            ToolSnippets[tool.Name] = promptSnippet;
        }
    }

    public void AddPromptGuideline(string guideline)
    {
        if (!string.IsNullOrWhiteSpace(guideline))
        {
            PromptGuidelines.Add(guideline.Trim());
        }
    }

    public void AddContextFile(string path, string content) => ContextFiles.Add(new CodingAgentContextFile(path, content));
}

public sealed class CodingAgentSession : IDisposable
{
    private readonly List<AgentEventHandler> _listeners = [];
    private readonly IReadOnlyList<ICodingAgentExtension> _extensions;
    private readonly Action _unsubscribe;
    private bool _disposed;

    private CodingAgentSession(
        AgentRuntime agent,
        string workingDirectory,
        string systemPrompt,
        IReadOnlyList<string> activeToolNames,
        IReadOnlyList<ICodingAgentExtension> extensions)
    {
        Agent = agent;
        WorkingDirectory = workingDirectory;
        SystemPrompt = systemPrompt;
        ActiveToolNames = activeToolNames;
        _extensions = extensions;
        _unsubscribe = agent.Subscribe(HandleAgentEventAsync);
    }

    public AgentRuntime Agent { get; }

    public AgentState State => Agent.State;

    public string WorkingDirectory { get; }

    public string SystemPrompt { get; }

    public IReadOnlyList<string> ActiveToolNames { get; }

    public static async Task<CodingAgentSession> CreateAsync(
        IChatClient chatClient,
        CodingAgentSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        options ??= new CodingAgentSessionOptions();

        var workingDirectory = Path.GetFullPath(options.WorkingDirectory);
        var builder = new CodingAgentSessionBuilder(
            workingDirectory,
            options.ActiveToolNames ?? BuiltInToolNames.Default);

        builder.CustomSystemPrompt = options.CustomSystemPrompt;
        builder.AppendSystemPrompt = options.AppendSystemPrompt;

        foreach (var promptGuideline in options.PromptGuidelines ?? Array.Empty<string>())
        {
            builder.AddPromptGuideline(promptGuideline);
        }

        foreach (var contextFile in options.ContextFiles ?? Array.Empty<CodingAgentContextFile>())
        {
            builder.ContextFiles.Add(contextFile);
        }

        foreach (var additionalTool in options.AdditionalTools ?? Array.Empty<AgentTool>())
        {
            builder.AddTool(additionalTool);
        }

        var extensions = options.Extensions?.ToArray() ?? Array.Empty<ICodingAgentExtension>();
        foreach (var extension in extensions)
        {
            await extension.ConfigureSessionAsync(builder, cancellationToken).ConfigureAwait(false);
        }

        var allBuiltInTools = CodingAgentTools.CreateAll(workingDirectory, options.ToolOptions);
        var activeToolNames = GetStableToolNameList(builder.ActiveToolNames);

        var toolSnippets = new Dictionary<string, string>(CodingAgentTools.PromptSnippets, StringComparer.Ordinal);
        foreach (var entry in builder.ToolSnippets)
        {
            toolSnippets[entry.Key] = entry.Value;
        }

        var finalTools = BuildFinalTools(activeToolNames, allBuiltInTools, builder.AdditionalTools);
        var systemPrompt = CodingAgentSystemPrompt.Build(
            new BuildSystemPromptOptions
            {
                CustomPrompt = builder.CustomSystemPrompt,
                AppendSystemPrompt = builder.AppendSystemPrompt,
                WorkingDirectory = workingDirectory,
                ContextFiles = builder.ContextFiles.ToArray(),
                PromptGuidelines = builder.PromptGuidelines.ToArray(),
                SelectedTools = finalTools.Select(static tool => tool.Name).ToArray(),
                ToolSnippets = toolSnippets,
            });

        var agent = new AgentRuntime(
            chatClient,
            new AgentOptions
            {
                Model = options.Model,
                SystemPrompt = systemPrompt,
                Tools = finalTools,
                Messages = options.Messages,
                ThinkingLevel = options.ThinkingLevel,
                ChatOptions = options.ChatOptions,
                ConvertToLlm = options.ConvertToLlm,
                TransformContext = options.TransformContext,
                BeforeToolCall = options.BeforeToolCall,
                AfterToolCall = options.AfterToolCall,
                SteeringMode = options.SteeringMode,
                FollowUpMode = options.FollowUpMode,
                ToolExecution = options.ToolExecution,
            });

        return new CodingAgentSession(
            agent,
            workingDirectory,
            systemPrompt,
            finalTools.Select(static tool => tool.Name).ToArray(),
            extensions);
    }

    public Action Subscribe(AgentEventHandler listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ThrowIfDisposed();

        _listeners.Add(listener);
        return () => _listeners.Remove(listener);
    }

    public Task PromptAsync(string text, IEnumerable<AIContent>? additionalContent = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Agent.PromptAsync(text, additionalContent, cancellationToken);
    }

    public Task PromptAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Agent.PromptAsync(message, cancellationToken);
    }

    public Task PromptAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Agent.PromptAsync(messages, cancellationToken);
    }

    public Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Agent.ContinueAsync(cancellationToken);
    }

    public void Steer(ChatMessage message)
    {
        ThrowIfDisposed();
        Agent.Steer(message);
    }

    public void FollowUp(ChatMessage message)
    {
        ThrowIfDisposed();
        Agent.FollowUp(message);
    }

    public void Abort()
    {
        ThrowIfDisposed();
        Agent.Abort();
    }

    public void Reset()
    {
        ThrowIfDisposed();
        Agent.Reset();
    }

    public bool HasQueuedMessages()
    {
        ThrowIfDisposed();
        return Agent.HasQueuedMessages();
    }

    public Task WaitForIdleAsync()
    {
        ThrowIfDisposed();
        return Agent.WaitForIdleAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _unsubscribe();
        _disposed = true;
    }

    private async ValueTask HandleAgentEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        foreach (var extension in _extensions)
        {
            await extension.OnAgentEventAsync(this, @event, cancellationToken).ConfigureAwait(false);
        }

        foreach (var listener in _listeners.ToArray())
        {
            await listener(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> GetStableToolNameList(IEnumerable<string> toolNames)
    {
        var stableToolNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var toolName in toolNames)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                continue;
            }

            if (seen.Add(toolName))
            {
                stableToolNames.Add(toolName);
            }
        }

        return stableToolNames;
    }

    private static IReadOnlyList<AgentTool> BuildFinalTools(
        IReadOnlyList<string> activeToolNames,
        IReadOnlyDictionary<string, AgentTool> builtInTools,
        IReadOnlyDictionary<string, AgentTool> additionalTools)
    {
        var finalTools = new List<AgentTool>();
        var finalToolMap = new Dictionary<string, AgentTool>(StringComparer.Ordinal);

        foreach (var toolName in activeToolNames)
        {
            if (additionalTools.TryGetValue(toolName, out var overridingTool))
            {
                finalToolMap[toolName] = overridingTool;
                continue;
            }

            if (!builtInTools.TryGetValue(toolName, out var builtInTool))
            {
                throw new InvalidOperationException($"Unknown tool '{toolName}'.");
            }

            finalToolMap[toolName] = builtInTool;
        }

        foreach (var additionalTool in additionalTools)
        {
            finalToolMap[additionalTool.Key] = additionalTool.Value;
        }

        foreach (var toolName in activeToolNames)
        {
            if (finalToolMap.TryGetValue(toolName, out var tool))
            {
                finalTools.Add(tool);
            }
        }

        foreach (var additionalTool in additionalTools)
        {
            if (activeToolNames.Contains(additionalTool.Key, StringComparer.Ordinal))
            {
                continue;
            }

            finalTools.Add(additionalTool.Value);
        }

        return finalTools;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
