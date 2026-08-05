using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Analytics;

/// <summary>
/// Provides analytics over cached usage statistics, such as daily and period summaries,
/// model distribution breakdowns, and session statistics.
/// </summary>
/// <param name="costCalculator">The calculator used to estimate token usage costs.</param>
public sealed class UsageAnalyzer(CostCalculator costCalculator)
{
    /// <summary>
    /// Builds a summary of usage activity for a single day.
    /// </summary>
    /// <param name="cache">The cached usage statistics to read from.</param>
    /// <param name="date">The date to summarize.</param>
    /// <returns>
    /// The <see cref="DailySummary"/> for the given date, or <see langword="null"/> if no
    /// activity was recorded for that date.
    /// </returns>
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

    /// <summary>
    /// Computes the token usage distribution and estimated cost across all models.
    /// </summary>
    /// <param name="cache">The cached usage statistics to read from.</param>
    /// <returns>
    /// A list of <see cref="ModelDistribution"/> entries ordered by total tokens descending,
    /// or an empty list if no tokens have been recorded.
    /// </returns>
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

    /// <summary>
    /// Aggregates daily summaries over an inclusive date range into a single period summary.
    /// </summary>
    /// <param name="cache">The cached usage statistics to read from.</param>
    /// <param name="from">The inclusive start date of the period.</param>
    /// <param name="to">The inclusive end date of the period.</param>
    /// <returns>A <see cref="PeriodSummary"/> aggregating totals for the requested period.</returns>
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

    /// <summary>
    /// Computes overall session statistics, including average messages per session and
    /// information about the longest recorded session.
    /// </summary>
    /// <param name="cache">The cached usage statistics to read from.</param>
    /// <returns>The computed <see cref="SessionStats"/>.</returns>
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

    /// <summary>
    /// Estimates the cost of daily token usage per model by approximating the split between
    /// input, output, and cache-related tokens.
    /// </summary>
    /// <param name="tokensByModel">The total token counts recorded for each model.</param>
    /// <returns>The estimated total cost across all models.</returns>
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