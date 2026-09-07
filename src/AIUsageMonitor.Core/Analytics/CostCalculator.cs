using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Analytics;

/// <summary>
/// Estimates the monetary cost (in USD) of model usage based on token counts
/// and a per-model pricing table.
/// </summary>
public sealed class CostCalculator
{
    /// <summary>
    /// Pricing per million tokens (input, output, cache read, cache creation) keyed by
    /// a substring that identifies the model name.
    /// </summary>
    private static readonly Dictionary<string, ModelPricing> PricingTable = new()
    {
        ["fable-5"] = new(10m, 50m, 1m, 12.5m, 20m),
        ["mythos-5"] = new(10m, 50m, 1m, 12.5m, 20m),
        ["opus-5"] = new(5m, 25m, 0.5m, 6.25m, 10m),
        ["opus-4"] = new(5m, 25m, 0.5m, 6.25m, 10m),
        ["sonnet-5"] = new(2m, 10m, 0.20m, 2.5m, 4m),
        ["sonnet-4"] = new(3m, 15m, 0.30m, 3.75m, 6m),
        ["haiku-4"] = new(1m, 5m, 0.10m, 1.25m, 2m),
    };

    /// <summary>
    /// Estimates the cost in USD for a request based on raw token counts, using the blended
    /// 5-minute cache-write rate for <paramref name="cacheCreationTokens"/> since the caller
    /// cannot distinguish 5-minute from 1-hour cache writes.
    /// </summary>
    /// <remarks>
    /// This is an intentional approximation, not a bug: <see cref="ModelUsageEntry.CacheCreationInputTokens"/>
    /// (Claude Code's own aggregated <c>stats-cache.json</c>) and older session-transcript
    /// lines only ever report a single combined cache-creation total — the 5-minute/1-hour
    /// split is simply not available at that layer, so there is nothing to route to a 1-hour
    /// rate even in principle. The 5-minute rate is used as the blended rate because it is
    /// the more common TTL. Callers that do have the split (raw transcript lines carrying
    /// <c>cache_creation</c>, see <see cref="Providers.Claude.Models.TokenUsage.CacheCreation"/>)
    /// should use the five-argument overload below instead for a precise estimate.
    /// </remarks>
    /// <param name="modelName">The model name (or a string containing it) used to resolve pricing.</param>
    /// <param name="inputTokens">Number of input tokens consumed.</param>
    /// <param name="outputTokens">Number of output tokens generated.</param>
    /// <param name="cacheReadTokens">Number of tokens read from cache.</param>
    /// <param name="cacheCreationTokens">Number of tokens used to create cache entries.</param>
    /// <returns>The estimated cost in USD, or 0 if the model's pricing could not be resolved.</returns>
    public decimal EstimateCost(string modelName, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens)
        => EstimateCost(modelName, inputTokens, outputTokens, cacheReadTokens, cacheCreationTokens, 0L);

    /// <summary>
    /// Estimates the cost in USD for a request based on raw token counts, pricing 5-minute and
    /// 1-hour cache writes separately.
    /// </summary>
    /// <param name="modelName">The model name (or a string containing it) used to resolve pricing.</param>
    /// <param name="inputTokens">Number of input tokens consumed.</param>
    /// <param name="outputTokens">Number of output tokens generated.</param>
    /// <param name="cacheReadTokens">Number of tokens read from cache.</param>
    /// <param name="cacheCreation5mTokens">Number of tokens used to create 5-minute-TTL cache entries.</param>
    /// <param name="cacheCreation1hTokens">Number of tokens used to create 1-hour-TTL cache entries.</param>
    /// <returns>The estimated cost in USD, or 0 if the model's pricing could not be resolved.</returns>
    public decimal EstimateCost(string modelName, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreation5mTokens, long cacheCreation1hTokens)
    {
        var pricing = ResolvePricing(modelName);
        if (pricing is null)
        {
            return 0m;
        }

        return (inputTokens * pricing.InputPerMTok
            + outputTokens * pricing.OutputPerMTok
            + cacheReadTokens * pricing.CacheReadPerMTok
            + cacheCreation5mTokens * pricing.CacheCreation5mPerMTok
            + cacheCreation1hTokens * pricing.CacheCreation1hPerMTok) / 1_000_000m;
    }

    public decimal EstimateCost(string modelName, ModelUsageEntry usage)
    {
        if (usage.CostUSD != 0)
        {
            return usage.CostUSD;
        }

        return EstimateCost(modelName, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadInputTokens, usage.CacheCreationInputTokens);
    }

    private static ModelPricing? ResolvePricing(string modelName)
    {
        foreach (var (key, pricing) in PricingTable)
        {
            if (modelName.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return pricing;
            }
        }

        return null;
    }

    private sealed record ModelPricing(
        decimal InputPerMTok,
        decimal OutputPerMTok,
        decimal CacheReadPerMTok,
        decimal CacheCreation5mPerMTok,
        decimal CacheCreation1hPerMTok);
}