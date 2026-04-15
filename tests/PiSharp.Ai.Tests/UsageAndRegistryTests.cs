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

    [Fact]
    public void ExtendedUsageDetails_AddUsage_AccumulatesTokenCounts()
    {
        var usage = new ExtendedUsageDetails();
        usage.AddUsage(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 });
        usage.AddUsage(new UsageDetails { InputTokenCount = 20, OutputTokenCount = 15 }, cacheWriteTokenCount: 8);

        Assert.Equal(30, usage.InputTokenCount);
        Assert.Equal(20, usage.OutputTokenCount);
        Assert.Equal(8, usage.CacheWriteTokenCount);
    }

    [Fact]
    public void ExtendedUsageDetails_FromUsage_WithNull_CreatesEmpty()
    {
        var usage = ExtendedUsageDetails.FromUsage(null);

        Assert.Null(usage.InputTokenCount);
        Assert.Null(usage.OutputTokenCount);
        Assert.Null(usage.CacheWriteTokenCount);
        Assert.Null(usage.Cost);
    }

    [Fact]
    public void UsageCostBreakdown_Zero_HasAllZeroCosts()
    {
        var zero = UsageCostBreakdown.Zero;

        Assert.Equal(0m, zero.InputCost);
        Assert.Equal(0m, zero.OutputCost);
        Assert.Equal(0m, zero.CacheReadCost);
        Assert.Equal(0m, zero.CacheWriteCost);
        Assert.Equal(0m, zero.TotalCost);
    }

    [Fact]
    public void UsageCostBreakdown_Add_WithNull_ReturnsSelf()
    {
        var breakdown = new UsageCostBreakdown(1m, 2m, 3m, 4m);
        var result = breakdown.Add(null);

        Assert.Equal(breakdown, result);
    }

    [Fact]
    public void ProviderRegistry_GetRequired_ThrowsForMissingProvider()
    {
        var registry = new ProviderRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("missing"));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void ProviderRegistry_Remove_ReturnsTrueForExistingProvider()
    {
        var registry = new ProviderRegistry();
        registry.Register(
            new ProviderConfiguration(ProviderId.OpenAi, ApiId.OpenAi, "OpenAI"),
            new StubChatClient());

        Assert.True(registry.Remove(ProviderId.OpenAi));
        Assert.False(registry.TryGet(ProviderId.OpenAi, out _));
    }

    [Fact]
    public void ProviderRegistry_Remove_ReturnsFalseForMissingProvider()
    {
        var registry = new ProviderRegistry();

        Assert.False(registry.Remove("nonexistent"));
    }

    [Fact]
    public void ProviderRegistry_Clear_RemovesAllProviders()
    {
        var registry = new ProviderRegistry();
        registry.Register(
            new ProviderConfiguration(ProviderId.OpenAi, ApiId.OpenAi, "OpenAI"),
            new StubChatClient());
        registry.Register(
            new ProviderConfiguration(ProviderId.Anthropic, ApiId.Anthropic, "Anthropic"),
            new StubChatClient());

        registry.Clear();

        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public void ModelRegistry_GetRequired_ThrowsForMissingModel()
    {
        var registry = new ModelRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired(ProviderId.OpenAi, "missing"));
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void ModelRegistry_GetByProvider_ReturnsEmptyForUnknownProvider()
    {
        var registry = new ModelRegistry();

        Assert.Empty(registry.GetByProvider("unknown"));
    }

    [Fact]
    public void ModelRegistry_Remove_ReturnsTrueForExistingModel()
    {
        var registry = new ModelRegistry();
        registry.Register(CreateTestModel("test-model", ProviderId.OpenAi));

        Assert.True(registry.Remove(ProviderId.OpenAi, "test-model"));
        Assert.False(registry.TryGet(ProviderId.OpenAi, "test-model", out _));
    }

    [Fact]
    public void ModelRegistry_GetAll_ReturnsModelsAcrossProviders()
    {
        var registry = new ModelRegistry();
        registry.Register(CreateTestModel("model-a", ProviderId.OpenAi));
        registry.Register(CreateTestModel("model-b", ProviderId.Anthropic));

        var all = registry.GetAll();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ModelMetadata_Supports_ReturnsFalseForMissingCapability()
    {
        var model = CreateTestModel("test", ProviderId.OpenAi);

        Assert.True(model.Supports(ModelCapability.TextInput));
        Assert.False(model.Supports(ModelCapability.Reasoning));
    }

    [Fact]
    public void ApiId_ImplicitConversions_WorkCorrectly()
    {
        ApiId id = "custom";
        string value = id;

        Assert.Equal("custom", id.Value);
        Assert.Equal("custom", value);
        Assert.Equal("custom", id.ToString());
    }

    [Fact]
    public void ProviderId_ImplicitConversions_WorkCorrectly()
    {
        ProviderId id = "custom-provider";
        string value = id;

        Assert.Equal("custom-provider", id.Value);
        Assert.Equal("custom-provider", value);
    }

    private static ModelMetadata CreateTestModel(string id, ProviderId providerId) =>
        new(id, id, ApiId.OpenAi, providerId, 128_000, 4_096,
            ModelCapability.TextInput | ModelCapability.Streaming,
            ModelPricing.Free);

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
