using PiSharp.Agent;

namespace PiSharp.Pods.Tests.Support;

internal sealed class FakePodAgentFactory(Func<PodEndpoint, PodAgentFactoryOptions?, PiSharp.Agent.Agent> create) : IPodAgentFactory
{
    private readonly Func<PodEndpoint, PodAgentFactoryOptions?, PiSharp.Agent.Agent> _create =
        create ?? throw new ArgumentNullException(nameof(create));

    public PiSharp.Agent.Agent Create(PodEndpoint endpoint, PodAgentFactoryOptions? options = null) =>
        _create(endpoint, options);
}
