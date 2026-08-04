using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Analytics;

public sealed class UsageAnalyzer(CostCalculator costCalculator)
{
    public DailySummary? GetDailySummary(StatsCache cache, DateOnly date)
    {
        var activity = cache.DailyActivity.FirstOrDefault(a => a.Date == date);
        var modelTokens = cache.DailyModelTokens.FirstOrDefault(t => t.Date == date);

        if (activity is null)
        {
            return null;
        }

        var tokensByModel = modelTokens?.TokensByModel ?? [];
        var totalTokens = tokensByModel.Values.Sum();
        var cost = EstimateDailyTokensCost(tokensByModel);

        return new(date, activity.MessageCount, activity.SessionCount,
            activity.ToolCallCount, totalTokens, new(tokensByModel), cost);
    }

    public PeriodSummary GetPeriodSummary(StatsCache cache, DateOnly from, DateOnly to)
    {
        var days = new List<DailySummary>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var summary = GetDailySummary(cache, date);
            if (summary is not null)
            {
                days.Add(summary);
            }
        }

        return new(
            from, to,
            days.Sum(d => d.Messages),
            days.Sum(d => d.Sessions),
            days.Sum(d => d.ToolCalls),
            days.Sum(d => d.TotalTokens),
            days.Sum(d => d.EstimatedCost),
            days);
    }

    public List<ModelDistribution> GetModelDistribution(StatsCache cache)
    {
        var totalAllTokens = cache.ModelUsage.Values
            .Sum(u => u.InputTokens + u.OutputTokens + u.CacheReadInputTokens + u.CacheCreationInputTokens);

        if (totalAllTokens == 0)
        {
            return [];
        }

        return cache.ModelUsage
            .Select(kvp =>
            {
                var total = kvp.Value.InputTokens + kvp.Value.OutputTokens
                    + kvp.Value.CacheReadInputTokens + kvp.Value.CacheCreationInputTokens;
                return new ModelDistribution(
                    kvp.Key,
                    kvp.Value.InputTokens,
                    kvp.Value.OutputTokens,
                    kvp.Value.CacheReadInputTokens,
                    kvp.Value.CacheCreationInputTokens,
                    total,
                    (double)total / totalAllTokens * 100,
                    costCalculator.EstimateCost(kvp.Key, kvp.Value));
            })
            .OrderByDescending(m => m.TotalTokens)
            .ToList();
    }

    public SessionStats GetSessionStats(StatsCache cache)
    {
        var longestDuration = cache.LongestSession is not null
            ? TimeSpan.FromMilliseconds(cache.LongestSession.Duration)
            : TimeSpan.Zero;

        return new(
            cache.TotalSessions,
            TimeSpan.Zero,
            cache.TotalSessions > 0 ? (double)cache.TotalMessages / cache.TotalSessions : 0,
            longestDuration,
            cache.LongestSession?.SessionId);
    }

    private decimal EstimateDailyTokensCost(Dictionary<string, long> tokensByModel)
    {
        var cost = 0m;
        foreach (var (model, tokens) in tokensByModel)
        {
            cost += costCalculator.EstimateCost(model, tokens / 4, tokens / 4, tokens / 2, 0);
        }
        return cost;
    }
}
