using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Adds up the token usage of one model, keeping the cache writes split by lifetime because the two are priced differently.
/// Every transcript aggregation counts and prices tokens through this type, so they cannot disagree.
/// </summary>
internal sealed class ModelTokenTotals
{
    /// <summary>
    /// Gets the input tokens.
    /// </summary>
    public long Input { get; private set; }

    /// <summary>
    /// Gets the output tokens.
    /// </summary>
    public long Output { get; private set; }

    /// <summary>
    /// Gets the tokens read from the prompt cache.
    /// </summary>
    public long CacheRead { get; private set; }

    /// <summary>
    /// Gets all tokens written to the prompt cache.
    /// </summary>
    public long CacheWrite { get; private set; }

    /// <summary>
    /// Gets the part of <see cref="CacheWrite"/> written with the 5-minute lifetime.
    /// </summary>
    public long CacheWrite5m { get; private set; }

    /// <summary>
    /// Gets the part of <see cref="CacheWrite"/> written with the 1-hour lifetime.
    /// </summary>
    public long CacheWrite1h { get; private set; }

    /// <summary>
    /// Gets the total tokens: input, output, cache reads and cache writes.
    /// </summary>
    public long Total => Input + Output + CacheRead + CacheWrite;

    /// <summary>
    /// Adds the usage reported for one response.
    /// </summary>
    /// <param name="usage">The usage to add.</param>
    public void Add(TokenUsage usage)
    {
        // A response without the per-lifetime split wrote all of its cache with the default 5-minute lifetime.
        var (cacheWrite5m, cacheWrite1h) = usage.CacheCreation is { } detail
            ? (detail.Ephemeral5mInputTokens, detail.Ephemeral1hInputTokens)
            : (usage.CacheCreationInputTokens, 0L);

        Input += usage.InputTokens;
        Output += usage.OutputTokens;
        CacheRead += usage.CacheReadInputTokens;
        CacheWrite += usage.CacheCreationInputTokens;
        CacheWrite5m += cacheWrite5m;
        CacheWrite1h += cacheWrite1h;
    }

    /// <summary>
    /// Estimates the cost of the accumulated usage.
    /// </summary>
    /// <param name="model">The model the usage belongs to.</param>
    /// <param name="costCalculator">The calculator that prices the tokens.</param>
    /// <returns>The estimated cost, in US dollars.</returns>
    public decimal EstimateCost(string model, CostCalculator costCalculator)
    {
        return costCalculator.EstimateCost(model, Input, Output, CacheRead, CacheWrite5m, CacheWrite1h);
    }

    /// <summary>
    /// Adds the usage of one response to the running totals of its model.
    /// </summary>
    /// <param name="byModel">The running totals per model.</param>
    /// <param name="model">The model that produced the response.</param>
    /// <param name="usage">The usage reported for the response.</param>
    /// <returns>The total tokens of the response, for callers that also keep their own sums.</returns>
    public static long Record(Dictionary<string, ModelTokenTotals> byModel, string model, TokenUsage usage)
    {
        if (!byModel.TryGetValue(model, out var totals))
        {
            totals = new ModelTokenTotals();
            byModel[model] = totals;
        }

        totals.Add(usage);
        return usage.TotalTokens;
    }

    /// <summary>
    /// Estimates the cost of the usage of several models.
    /// </summary>
    /// <param name="byModel">The running totals per model.</param>
    /// <param name="costCalculator">The calculator that prices the tokens.</param>
    /// <returns>The summed estimated cost, in US dollars.</returns>
    public static decimal EstimateCost(Dictionary<string, ModelTokenTotals> byModel, CostCalculator costCalculator)
    {
        return byModel.Sum(pair => pair.Value.EstimateCost(pair.Key, costCalculator));
    }
}
