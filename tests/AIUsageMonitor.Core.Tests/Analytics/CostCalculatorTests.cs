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

        Assert.Equal(2m, cost);
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

        Assert.Equal(0.20m + 2.5m, cost);
    }

    [Fact]
    public void EstimateCost_IsCaseInsensitive()
    {
        var cost = _sut.EstimateCost("CLAUDE-SONNET-5", 1_000_000, 0, 0, 0);

        Assert.Equal(2m, cost);
    }

    [Theory]
    [InlineData("claude-opus-5-20260101", 5.0, 25.0)]
    [InlineData("claude-sonnet-5-20260101", 2.0, 10.0)]
    [InlineData("claude-haiku-4-5-20251001", 1.0, 5.0)]
    [InlineData("claude-opus-5-5", 4.0, 20.0)]
    [InlineData("claude-opus-4-5-20251101", 5.0, 25.0)]
    [InlineData("claude-opus-4-1-20250805", 15.0, 75.0)]
    [InlineData("claude-opus-4-20250514", 15.0, 75.0)]
    [InlineData("claude-fable-5-1", 10.0, 50.0)]
    [InlineData("claude-haiku-3-5-20241022", 0.8, 4.0)]
    public void EstimateCost_ResolvesPricingByModelSubstring(string modelName, double inputPerMTok, double outputPerMTok)
    {
        var cost = _sut.EstimateCost(modelName, inputTokens: 1_000_000, outputTokens: 1_000_000,
            cacheReadTokens: 0, cacheCreationTokens: 0);

        Assert.Equal((decimal)(inputPerMTok + outputPerMTok), cost);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 0.20)]
    [InlineData("claude-fable-5-1", 0.25)]
    [InlineData("claude-fable-5", 1.0)]
    public void EstimateCost_CacheReadRate_MatchesModelSpecificPrice(string modelName, double cacheReadPerMTok)
    {
        var cost = _sut.EstimateCost(modelName, inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 1_000_000, cacheCreationTokens: 0);

        Assert.Equal((decimal)cacheReadPerMTok, cost);
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

    [Fact]
    public void EstimateCost_WithSplitCacheCreation_Prices1hWriteAtTwiceInput()
    {
        var cost = _sut.EstimateCost("sonnet-5", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 0, cacheCreation5mTokens: 0, cacheCreation1hTokens: 1_000_000);

        Assert.Equal(4m, cost);
    }

    [Fact]
    public void EstimateCost_WithSplitCacheCreation_Combines5mAnd1hWrites()
    {
        var cost = _sut.EstimateCost("sonnet-5", inputTokens: 0, outputTokens: 0,
            cacheReadTokens: 0, cacheCreation5mTokens: 1_000_000, cacheCreation1hTokens: 1_000_000);

        Assert.Equal(2.5m + 4m, cost);
    }

    [Fact]
    public void EstimateCost_WithoutSplit_MatchesSplitOverloadWhenAll1hIsZero()
    {
        var blended = _sut.EstimateCost("sonnet-5", 1_000_000, 0, 0, cacheCreationTokens: 1_000_000);
        var split = _sut.EstimateCost("sonnet-5", 1_000_000, 0, 0, cacheCreation5mTokens: 1_000_000, cacheCreation1hTokens: 0);

        Assert.Equal(blended, split);
    }

    /// <summary>
    /// Verifies that Claude 3 models, whose names put the version before the family, are priced rather than costed at zero.
    /// </summary>
    /// <param name="model">The model name as it appears in a transcript.</param>
    /// <param name="expectedInputPerMTok">The expected input price per million tokens.</param>
    [Theory]
    [InlineData("claude-3-7-sonnet-20250219", 3)]
    [InlineData("claude-3-5-sonnet-20241022", 3)]
    [InlineData("claude-3-5-haiku-20241022", 0.8)]
    [InlineData("claude-3-opus-20240229", 15)]
    [InlineData("claude-3-haiku-20240307", 0.25)]
    public void EstimateCost_ClaudeThreeModelName_UsesThatModelsInputPrice(string model, double expectedInputPerMTok)
    {
        var cost = _sut.EstimateCost(model, inputTokens: 1_000_000, outputTokens: 0, cacheReadTokens: 0, cacheCreationTokens: 0);

        Assert.Equal((decimal)expectedInputPerMTok, cost);
    }
}
