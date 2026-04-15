using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Ai.Tests;

public sealed class UsageAndRegistryTests
{
    [Fact]
    public void ExtendedUsageDetails_TracksCacheWriteAndCost()
    {
        var usage = ExtendedUsageDetails.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 50,
                CachedInputTokenCount = 20,
            },
            cacheWriteTokenCount: 10);

        var cost = usage.ApplyPricing(new ModelPricing(1.5m, 6m, 0.3m, 0.9m));

        Assert.Equal(20, usage.CacheReadTokenCount);
        Assert.Equal(10, usage.CacheWriteTokenCount);
        Assert.Equal(0.00015m, cost.InputCost);
        Assert.Equal(0.0003m, cost.OutputCost);
        Assert.Equal(0.000006m, cost.CacheReadCost);
        Assert.Equal(0.000009m, cost.CacheWriteCost);
        Assert.Equal(0.000465m, cost.TotalCost);
        Assert.Equal(10, usage.AdditionalCounts![ExtendedUsageDetails.CacheWriteTokenCountKey]);
    }

    [Fact]
    public void ProviderRegistry_RegistersAndFiltersProvidersByApi()
    {
        var registry = new ProviderRegistry();
        var openAiRegistration = registry.Register(
            new ProviderConfiguration(
                ProviderId.OpenAi,
                ApiId.OpenAi,
                "OpenAI"),
            new StubChatClient());
        var anthropicRegistration = registry.Register(
            new ProviderConfiguration(
                ProviderId.Anthropic,
                ApiId.Anthropic,
                "Anthropic"),
            new StubChatClient());

        Assert.Same(openAiRegistration, registry.GetRequired(ProviderId.OpenAi));

        var providers = registry.GetByApi(ApiId.Anthropic);
        var provider = Assert.Single(providers);
        Assert.Same(anthropicRegistration, provider);
    }

    [Fact]
    public void ModelRegistry_StoresMetadataAndCalculatesCost()
    {
        var registry = new ModelRegistry();
        var model = registry.Register(
            new ModelMetadata(
                "gpt-4.1-mini",
                "GPT-4.1 mini",
                ApiId.OpenAi,
                ProviderId.OpenAi,
                1_000_000,
                32_768,
                ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                new ModelPricing(0.4m, 1.6m, 0.1m, 0.8m)));

        var usage = ExtendedUsageDetails.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 10_000,
                OutputTokenCount = 5_000,
                CachedInputTokenCount = 2_000,
            },
            cacheWriteTokenCount: 1_000);

        var cost = registry.CalculateCost(model, usage);

        Assert.True(model.Supports(ModelCapability.ToolCalling));
        Assert.Equal(model, registry.GetRequired(ProviderId.OpenAi, "gpt-4.1-mini"));
        Assert.Equal(0.004m, cost.InputCost);
        Assert.Equal(0.008m, cost.OutputCost);
        Assert.Equal(0.0002m, cost.CacheReadCost);
        Assert.Equal(0.0008m, cost.CacheWriteCost);
        Assert.Equal(0.013m, cost.TotalCost);
    }

    private sealed class StubChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
