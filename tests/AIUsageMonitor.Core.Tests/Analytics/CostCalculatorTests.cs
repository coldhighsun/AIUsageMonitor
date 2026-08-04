using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Analytics;

public class CostCalculatorTests
{
    private readonly CostCalculator _sut = new();

    [Fact]
    public void EstimateCost_FromModelUsageEntry_EstimatesWhenRecordedCostIsZero()
    {
        var usage = new ModelUsageEntry { CostUSD = 0m, InputTokens = 1_000_000 };

        var cost = _sut.EstimateCost("sonnet-5", usage);

        Assert.Equal(3m, cost);
    }

    [Fact]
    public void EstimateCost_FromModelUsageEntry_PrefersRecordedCostWhenNonZero()
    {
        var usage = new ModelUsageEntry { CostUSD = 42m, InputTokens = 1_000_000 };

        var cost = _sut.EstimateCost("sonnet-5", usage);

        Assert.Equal(42m, cost);
    }

    [Fact]
    public void EstimateCost_IncludesCacheReadAndCreationTokens()
    {
        var cost = _sut.EstimateCost("sonnet-5", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 1_000_000, cacheCreationTokens: 1_000_000);

        Assert.Equal(0.30m + 3.75m, cost);
    }

    [Fact]
    public void EstimateCost_IsCaseInsensitive()
    {
        var cost = _sut.EstimateCost("CLAUDE-SONNET-5", 1_000_000, 0, 0, 0);

        Assert.Equal(3m, cost);
    }

    [Theory]
    [InlineData("claude-opus-5-20260101", 5.0, 25.0)]
    [InlineData("claude-sonnet-5-20260101", 3.0, 15.0)]
    [InlineData("claude-haiku-4-5-20251001", 1.0, 5.0)]
    public void EstimateCost_ResolvesPricingByModelSubstring(string modelName, double inputPerMTok, double outputPerMTok)
    {
        var cost = _sut.EstimateCost(modelName, inputTokens: 1_000_000, outputTokens: 1_000_000,
            cacheReadTokens: 0, cacheCreationTokens: 0);

        Assert.Equal((decimal)(inputPerMTok + outputPerMTok), cost);
    }

    [Fact]
    public void EstimateCost_UnknownModel_ReturnsZero()
    {
        var cost = _sut.EstimateCost("some-unknown-model", 1_000_000, 1_000_000, 0, 0);

        Assert.Equal(0m, cost);
    }

    [Fact]
    public void EstimateCost_ZeroTokens_ReturnsZero()
    {
        var cost = _sut.EstimateCost("sonnet-5", 0, 0, 0, 0);

        Assert.Equal(0m, cost);
    }
}