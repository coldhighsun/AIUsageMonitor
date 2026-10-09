using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for <see cref="ModelTokenTotals"/>, which every transcript aggregation uses to count and price tokens.
/// </summary>
public class ModelTokenTotalsTests
{
    /// <summary>
    /// Creates a usage with a cache-write split.
    /// </summary>
    /// <returns>The usage.</returns>
    private static TokenUsage SplitUsage()
    {
        return new TokenUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            CacheReadInputTokens = 1000,
            CacheCreationInputTokens = 500,
            CacheCreation = new CacheCreationDetail { Ephemeral5mInputTokens = 200, Ephemeral1hInputTokens = 300 },
        };
    }

    /// <summary>
    /// Verifies that a usage with a split adds each lifetime to its own counter.
    /// </summary>
    [Fact]
    public void Add_UsageWithCacheSplit_KeepsTheLifetimesApart()
    {
        var totals = new ModelTokenTotals();

        totals.Add(SplitUsage());

        Assert.Equal(100, totals.Input);
        Assert.Equal(50, totals.Output);
        Assert.Equal(1000, totals.CacheRead);
        Assert.Equal(500, totals.CacheWrite);
        Assert.Equal(200, totals.CacheWrite5m);
        Assert.Equal(300, totals.CacheWrite1h);
        Assert.Equal(1650, totals.Total);
    }

    /// <summary>
    /// Verifies that a response's total counts all four kinds of token once.
    /// </summary>
    [Fact]
    public void TotalTokens_UsageWithAllKinds_SumsInputOutputCacheReadAndCacheWrite()
    {
        Assert.Equal(1650, SplitUsage().TotalTokens);
        Assert.Equal(0, new TokenUsage().TotalTokens);
    }

    /// <summary>
    /// Verifies that a usage without a split is priced as 5-minute cache writes.
    /// </summary>
    [Fact]
    public void Add_UsageWithoutCacheSplit_CountsAllCacheWritesAsFiveMinute()
    {
        var totals = new ModelTokenTotals();

        totals.Add(new TokenUsage { CacheCreationInputTokens = 400 });

        Assert.Equal(400, totals.CacheWrite);
        Assert.Equal(400, totals.CacheWrite5m);
        Assert.Equal(0, totals.CacheWrite1h);
    }

    /// <summary>
    /// Verifies that recording returns the tokens of that response and accumulates per model.
    /// </summary>
    [Fact]
    public void Record_TwoResponses_ReturnsEachResponsesTokensAndAccumulates()
    {
        var byModel = new Dictionary<string, ModelTokenTotals>();

        var first = ModelTokenTotals.Record(byModel, "sonnet-5", SplitUsage());
        var second = ModelTokenTotals.Record(byModel, "sonnet-5", new TokenUsage { InputTokens = 7 });

        Assert.Equal(1650, first);
        Assert.Equal(7, second);
        Assert.Equal(1657, byModel["sonnet-5"].Total);
    }

    /// <summary>
    /// Verifies that the cost is the calculator price of the split counters, summed over models.
    /// </summary>
    [Fact]
    public void EstimateCost_TwoModels_SumsTheCalculatorPrices()
    {
        var costs = new CostCalculator();
        var byModel = new Dictionary<string, ModelTokenTotals>();
        ModelTokenTotals.Record(byModel, "sonnet-5", SplitUsage());
        ModelTokenTotals.Record(byModel, "claude-opus-5-5", new TokenUsage { InputTokens = 10, OutputTokens = 20 });

        var cost = ModelTokenTotals.EstimateCost(byModel, costs);

        Assert.Equal(costs.EstimateCost("sonnet-5", 100, 50, 1000, 200, 300) + costs.EstimateCost("claude-opus-5-5", 10, 20, 0, 0, 0), cost);
        Assert.Equal(costs.EstimateCost("sonnet-5", 100, 50, 1000, 200, 300), byModel["sonnet-5"].EstimateCost("sonnet-5", costs));
    }
}
