using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public interface ICodingAgentExtension
{
    ValueTask ConfigureSessionAsync(
        CodingAgentSessionBuilder builder,
        IExtensionApi api,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    ValueTask OnAgentEventAsync(CodingAgentSession session, AgentEvent @event, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
