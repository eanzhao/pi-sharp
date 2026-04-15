using Microsoft.Extensions.AI;
using PiSharp.Agent;
using AgentRuntime = PiSharp.Agent.Agent;

namespace PiSharp.WebUi;

public sealed class AgentViewModel : IDisposable
{
    private readonly Action _unsubscribe;
    private bool _disposed;

    public AgentViewModel(AgentRuntime agent)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _unsubscribe = agent.Subscribe(HandleAgentEventAsync);
    }

    public event EventHandler? Changed;

    public AgentRuntime Agent { get; }

    public AgentState State => Agent.State;

    public IReadOnlyList<ChatMessage> Messages => State.Messages;

    public ChatMessage? StreamingMessage => State.StreamingMessage;

    public IReadOnlySet<string> PendingToolCalls => State.PendingToolCalls;

    public bool IsStreaming => State.IsStreaming;

    public string? ErrorMessage => State.ErrorMessage;

    public Task SendAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return Agent.PromptAsync(prompt, cancellationToken: cancellationToken);
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

    private ValueTask HandleAgentEventAsync(AgentEvent _, CancellationToken __)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
}
