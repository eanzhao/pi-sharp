using PiSharp.Pods.Providers;

namespace PiSharp.Pods.Tests;

public sealed class GpuProviderRegistryTests
{
    [Fact]
    public void GetAll_ReturnsKnownProviders()
    {
        var registry = new GpuProviderRegistry();

        var providers = registry.GetAll();

        Assert.Contains(providers, provider => provider is DataCrunchProvider);
        Assert.Contains(providers, provider => provider is RunPodProvider);
        Assert.Contains(providers, provider => provider is VastAiProvider);
        Assert.All(providers, provider => Assert.IsAssignableFrom<IGpuProvider>(provider));
    }

    [Fact]
    public void TryGet_SupportsAliases()
    {
        var registry = new GpuProviderRegistry();

        Assert.True(registry.TryGet("data-crunch", out var dataCrunch));
        Assert.True(registry.TryGet("vast.ai", out var vastAi));

        Assert.IsType<DataCrunchProvider>(dataCrunch);
        Assert.IsType<VastAiProvider>(vastAi);
    }
}
