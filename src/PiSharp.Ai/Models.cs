using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace PiSharp.Ai;

[Flags]
public enum ModelCapability
{
    None = 0,
    TextInput = 1 << 0,
    ImageInput = 1 << 1,
    Streaming = 1 << 2,
    ToolCalling = 1 << 3,
    Reasoning = 1 << 4,
    PromptCaching = 1 << 5,
}

public sealed record TokenPricing
{
    public TokenPricing(decimal firstPerMillionTokens)
        : this(firstPerMillionTokens, null, null)
    {
    }

    public TokenPricing(
        decimal firstPerMillionTokens,
        long? firstTierTokenLimit,
        decimal? additionalPerMillionTokens)
    {
        FirstPerMillionTokens = firstPerMillionTokens;
        FirstTierTokenLimit = firstTierTokenLimit is > 0 ? firstTierTokenLimit : null;
        AdditionalPerMillionTokens = additionalPerMillionTokens ?? firstPerMillionTokens;
    }

    public decimal FirstPerMillionTokens { get; init; }

    public long? FirstTierTokenLimit { get; init; }

    public decimal AdditionalPerMillionTokens { get; init; }
}

public sealed record ModelPricing
{
    public ModelPricing(
        decimal inputPerMillionTokens,
        decimal outputPerMillionTokens,
        decimal cacheReadPerMillionTokens = 0m,
        decimal cacheWritePerMillionTokens = 0m)
        : this(
            new TokenPricing(inputPerMillionTokens),
            new TokenPricing(outputPerMillionTokens),
            new TokenPricing(cacheReadPerMillionTokens),
            new TokenPricing(cacheWritePerMillionTokens))
    {
    }

    public ModelPricing(
        TokenPricing inputPricing,
        TokenPricing outputPricing,
        TokenPricing? cacheReadPricing = null,
        TokenPricing? cacheWritePricing = null)
    {
        InputPricing = inputPricing ?? throw new ArgumentNullException(nameof(inputPricing));
        OutputPricing = outputPricing ?? throw new ArgumentNullException(nameof(outputPricing));
        CacheReadPricing = cacheReadPricing ?? new TokenPricing(0m);
        CacheWritePricing = cacheWritePricing ?? new TokenPricing(0m);
    }

    public static ModelPricing Free { get; } = new(0m, 0m);

    public TokenPricing InputPricing { get; init; }

    public TokenPricing OutputPricing { get; init; }

    public TokenPricing CacheReadPricing { get; init; }

    public TokenPricing CacheWritePricing { get; init; }

    public decimal InputPerMillionTokens => InputPricing.FirstPerMillionTokens;

    public decimal OutputPerMillionTokens => OutputPricing.FirstPerMillionTokens;

    public decimal CacheReadPerMillionTokens => CacheReadPricing.FirstPerMillionTokens;

    public decimal CacheWritePerMillionTokens => CacheWritePricing.FirstPerMillionTokens;
}

public sealed record ModelMetadata(
    string Id,
    string Name,
    ApiId ApiId,
    ProviderId ProviderId,
    int ContextWindow,
    int MaxOutputTokens,
    ModelCapability Capabilities,
    ModelPricing Pricing)
{
    public bool Supports(ModelCapability capability) => (Capabilities & capability) == capability;
}

public sealed class ModelRegistry
{
    private readonly ConcurrentDictionary<ProviderId, ConcurrentDictionary<string, ModelMetadata>> _models = new();

    public ModelMetadata Register(ModelMetadata model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var providerModels = _models.GetOrAdd(model.ProviderId, _ => new ConcurrentDictionary<string, ModelMetadata>(StringComparer.Ordinal));
        providerModels[model.Id] = model;
        return model;
    }

    public bool TryGet(ProviderId providerId, string modelId, [NotNullWhen(true)] out ModelMetadata? model)
    {
        model = null;

        if (!_models.TryGetValue(providerId, out var providerModels))
        {
            return false;
        }

        return providerModels.TryGetValue(modelId, out model);
    }

    public ModelMetadata GetRequired(ProviderId providerId, string modelId)
    {
        if (TryGet(providerId, modelId, out var model))
        {
            return model;
        }

        throw new KeyNotFoundException($"No model named '{modelId}' is registered for provider '{providerId.Value}'.");
    }

    public IReadOnlyCollection<ModelMetadata> GetByProvider(ProviderId providerId)
    {
        if (!_models.TryGetValue(providerId, out var providerModels))
        {
            return Array.Empty<ModelMetadata>();
        }

        return providerModels.Values
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyCollection<ModelMetadata> GetAll() =>
        _models.Values
            .SelectMany(providerModels => providerModels.Values)
            .OrderBy(model => model.ProviderId.Value, StringComparer.Ordinal)
            .ThenBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();

    public UsageCostBreakdown CalculateCost(ModelMetadata model, UsageDetails usage)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        return UsageCostBreakdown.Calculate(usage, model.Pricing);
    }

    public bool Remove(ProviderId providerId, string modelId)
    {
        if (!_models.TryGetValue(providerId, out var providerModels))
        {
            return false;
        }

        return providerModels.TryRemove(modelId, out _);
    }

    public void Clear() => _models.Clear();
}
