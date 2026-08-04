using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Analytics;

public class UsageAnalyzerTests
{
    private readonly UsageAnalyzer _sut = new(new());

    [Fact]
    public void GetDailySummary_AggregatesActivityAndTokens()
    {
        var cache = BuildCache();

        var summary = _sut.GetDailySummary(cache, new(2026, 8, 1));

        Assert.NotNull(summary);
        Assert.Equal(10, summary.Messages);
        Assert.Equal(2, summary.Sessions);
        Assert.Equal(5, summary.ToolCalls);
        Assert.Equal(1000, summary.TotalTokens);
        Assert.Equal(1000, summary.TokensByModel["sonnet-5"]);
    }

    [Fact]
    public void GetDailySummary_NoModelTokens_TotalTokensIsZero()
    {
        var cache = BuildCache();

        var summary = _sut.GetDailySummary(cache, new(2026, 8, 2));

        Assert.NotNull(summary);
        Assert.Equal(0, summary.TotalTokens);
        Assert.Empty(summary.TokensByModel);
    }

    [Fact]
    public void GetDailySummary_ReturnsNull_WhenNoActivityForDate()
    {
        var cache = BuildCache();

        var summary = _sut.GetDailySummary(cache, new(2026, 8, 5));

        Assert.Null(summary);
    }

    [Fact]
    public void GetModelDistribution_ComputesPercentagesThatSumToOneHundred()
    {
        var cache = BuildCache();

        var distribution = _sut.GetModelDistribution(cache);

        Assert.Equal(2, distribution.Count);
        Assert.Equal(100.0, distribution.Sum(d => d.Percentage), precision: 5);
        // sonnet-5: 500 tokens, opus-5: 500 tokens -> equal, opus first alphabetically by tie order is stable by original order
        Assert.Contains(distribution, d => d.ModelName == "sonnet-5" && d.TotalTokens == 500);
        Assert.Contains(distribution, d => d.ModelName == "opus-5" && d.TotalTokens == 500);
    }

    [Fact]
    public void GetModelDistribution_NoUsage_ReturnsEmpty()
    {
        var cache = new StatsCache();

        var distribution = _sut.GetModelDistribution(cache);

        Assert.Empty(distribution);
    }

    [Fact]
    public void GetPeriodSummary_SumsAcrossDaysAndSkipsMissingDates()
    {
        var cache = BuildCache();

        var period = _sut.GetPeriodSummary(cache, new(2026, 8, 1), new(2026, 8, 3));

        Assert.Equal(30, period.TotalMessages);
        Assert.Equal(5, period.TotalSessions);
        Assert.Equal(13, period.TotalToolCalls);
        Assert.Equal(2, period.DailyBreakdown.Count);
    }

    [Fact]
    public void GetSessionStats_ComputesAveragesAndLongestSession()
    {
        var cache = BuildCache();

        var stats = _sut.GetSessionStats(cache);

        Assert.Equal(5, stats.Total);
        Assert.Equal(6.0, stats.AvgMessages);
        Assert.Equal(TimeSpan.FromMilliseconds(60_000), stats.LongestDuration);
        Assert.Equal("abc", stats.LongestSessionId);
    }

    [Fact]
    public void GetSessionStats_NoSessions_AvgMessagesIsZero()
    {
        var cache = new StatsCache { TotalSessions = 0, TotalMessages = 0 };

        var stats = _sut.GetSessionStats(cache);

        Assert.Equal(0, stats.AvgMessages);
        Assert.Null(stats.LongestSessionId);
    }

    private static StatsCache BuildCache() => new()
    {
        DailyActivity =
        [
            new() { Date = new(2026, 8, 1), MessageCount = 10, SessionCount = 2, ToolCallCount = 5 },
            new() { Date = new(2026, 8, 2), MessageCount = 20, SessionCount = 3, ToolCallCount = 8 },
        ],
        DailyModelTokens =
        [
            new()
            {
                Date = new(2026, 8, 1),
                TokensByModel = new() { ["sonnet-5"] = 1000 },
            },
        ],
        ModelUsage = new()
        {
            ["sonnet-5"] = new() { InputTokens = 300, OutputTokens = 100, CacheReadInputTokens = 50, CacheCreationInputTokens = 50 },
            ["opus-5"] = new() { InputTokens = 400, OutputTokens = 100, CacheReadInputTokens = 0, CacheCreationInputTokens = 0 },
        },
        HourCounts = new() { ["9"] = 4, ["14"] = 2 },
        TotalSessions = 5,
        TotalMessages = 30,
        LongestSession = new() { SessionId = "abc", Duration = 60_000, MessageCount = 12, Timestamp = "2026-08-02T00:00:00Z" },
    };
}