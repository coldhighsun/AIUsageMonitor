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
        ["fable-5"] = new(10m, 50m, 1m, 12.5m),
        ["mythos-5"] = new(10m, 50m, 1m, 12.5m),
        ["opus-5"] = new(5m, 25m, 0.5m, 6.25m),
        ["opus-4"] = new(15m, 75m, 1.875m, 18.75m),
        ["sonnet-5"] = new(3m, 15m, 0.30m, 3.75m),
        ["sonnet-4"] = new(3m, 15m, 0.30m, 3.75m),
        ["haiku-4"] = new(1m, 5m, 0.10m, 1.25m),
    };

    /// <summary>
    /// Estimates the cost in USD for a request based on raw token counts.
    /// </summary>
    /// <param name="modelName">The model name (or a string containing it) used to resolve pricing.</param>
    /// <param name="inputTokens">Number of input tokens consumed.</param>
    /// <param name="outputTokens">Number of output tokens generated.</param>
    /// <param name="cacheReadTokens">Number of tokens read from cache.</param>
    /// <param name="cacheCreationTokens">Number of tokens used to create cache entries.</param>
    /// <returns>The estimated cost in USD, or 0 if the model's pricing could not be resolved.</returns>
    public decimal EstimateCost(string modelName, long inputTokens, long outputTokens,
        long cacheReadTokens, long cacheCreationTokens)
    {
        var pricing = ResolvePricing(modelName);
        if (pricing is null)
        {
            return 0m;
        }

        return (inputTokens * pricing.InputPerMTok
            + outputTokens * pricing.OutputPerMTok
            + cacheReadTokens * pricing.CacheReadPerMTok
            + cacheCreationTokens * pricing.CacheCreationPerMTok) / 1_000_000m;
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
        decimal CacheCreationPerMTok);
}