using Microsoft.Extensions.AI;

namespace PiSharp.Ai;

public sealed record UsageCostBreakdown(
    decimal InputCost,
    decimal OutputCost,
    decimal CacheReadCost,
    decimal CacheWriteCost)
{
    public decimal TotalCost => InputCost + OutputCost + CacheReadCost + CacheWriteCost;

    public static UsageCostBreakdown Zero { get; } = new(0m, 0m, 0m, 0m);

    public UsageCostBreakdown Add(UsageCostBreakdown? other)
    {
        if (other is null)
        {
            return this;
        }

        return new UsageCostBreakdown(
            InputCost + other.InputCost,
            OutputCost + other.OutputCost,
            CacheReadCost + other.CacheReadCost,
            CacheWriteCost + other.CacheWriteCost);
    }

    public static UsageCostBreakdown Calculate(UsageDetails usage, ModelPricing pricing)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(pricing);

        var cacheWriteTokenCount = usage is ExtendedUsageDetails extendedUsage
            ? extendedUsage.CacheWriteTokenCount
            : null;

        return new UsageCostBreakdown(
            ScaleCost(usage.InputTokenCount, pricing.InputPerMillionTokens),
            ScaleCost(usage.OutputTokenCount, pricing.OutputPerMillionTokens),
            ScaleCost(usage.CachedInputTokenCount, pricing.CacheReadPerMillionTokens),
            ScaleCost(cacheWriteTokenCount, pricing.CacheWritePerMillionTokens));
    }

    private static decimal ScaleCost(long? tokenCount, decimal pricePerMillionTokens)
    {
        if (tokenCount is null || tokenCount <= 0 || pricePerMillionTokens <= 0m)
        {
            return 0m;
        }

        return tokenCount.Value * pricePerMillionTokens / 1_000_000m;
    }
}

public sealed class ExtendedUsageDetails : UsageDetails
{
    public const string CacheWriteTokenCountKey = "pi-sharp.cache_write_tokens";

    public ExtendedUsageDetails()
    {
        AdditionalCounts = new AdditionalPropertiesDictionary<long>();
    }

    public ExtendedUsageDetails(UsageDetails? usageDetails, long? cacheWriteTokenCount = null)
        : this()
    {
        CopyFrom(usageDetails);
        CacheWriteTokenCount = cacheWriteTokenCount
            ?? (usageDetails as ExtendedUsageDetails)?.CacheWriteTokenCount
            ?? TryGetAdditionalCount(usageDetails, CacheWriteTokenCountKey);
        Cost = (usageDetails as ExtendedUsageDetails)?.Cost;
        SynchronizeAdditionalCounts();
    }

    public long? CacheReadTokenCount => CachedInputTokenCount;

    public long? CacheWriteTokenCount { get; set; }

    public UsageCostBreakdown? Cost { get; set; }

    public static ExtendedUsageDetails FromUsage(UsageDetails? usageDetails, long? cacheWriteTokenCount = null) =>
        new(usageDetails, cacheWriteTokenCount);

    public void AddUsage(UsageDetails? usageDetails, long? cacheWriteTokenCount = null)
    {
        if (usageDetails is not null)
        {
            Add(usageDetails);
        }

        CacheWriteTokenCount = SumNullable(
            CacheWriteTokenCount,
            cacheWriteTokenCount
                ?? (usageDetails as ExtendedUsageDetails)?.CacheWriteTokenCount
                ?? TryGetAdditionalCount(usageDetails, CacheWriteTokenCountKey));

        if (usageDetails is ExtendedUsageDetails { Cost: not null } extendedUsage)
        {
            Cost = (Cost ?? UsageCostBreakdown.Zero).Add(extendedUsage.Cost);
        }

        SynchronizeAdditionalCounts();
    }

    public UsageCostBreakdown ApplyPricing(ModelPricing pricing)
    {
        Cost = UsageCostBreakdown.Calculate(this, pricing);
        return Cost;
    }

    private void CopyFrom(UsageDetails? usageDetails)
    {
        if (usageDetails is null)
        {
            return;
        }

        InputTokenCount = usageDetails.InputTokenCount;
        OutputTokenCount = usageDetails.OutputTokenCount;
        TotalTokenCount = usageDetails.TotalTokenCount;
        CachedInputTokenCount = usageDetails.CachedInputTokenCount;
        ReasoningTokenCount = usageDetails.ReasoningTokenCount;

        foreach (var pair in usageDetails.AdditionalCounts ?? [])
        {
            AdditionalCounts![pair.Key] = pair.Value;
        }
    }

    private void SynchronizeAdditionalCounts()
    {
        if (CacheWriteTokenCount is long cacheWriteTokenCount)
        {
            AdditionalCounts![CacheWriteTokenCountKey] = cacheWriteTokenCount;
        }
        else
        {
            AdditionalCounts!.Remove(CacheWriteTokenCountKey);
        }
    }

    private static long? TryGetAdditionalCount(UsageDetails? usageDetails, string key)
    {
        if (usageDetails?.AdditionalCounts is not null &&
            usageDetails.AdditionalCounts.TryGetValue(key, out var value))
        {
            return value;
        }

        return null;
    }

    private static long? SumNullable(long? left, long? right)
    {
        if (!left.HasValue && !right.HasValue)
        {
            return null;
        }

        return (left ?? 0) + (right ?? 0);
    }
}
