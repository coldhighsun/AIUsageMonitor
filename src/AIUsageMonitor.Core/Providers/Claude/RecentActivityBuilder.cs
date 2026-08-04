using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Aggregates raw session transcripts into a rolling "last N hours" activity
/// summary, since <see cref="Models.StatsCache"/> only tracks hour-of-day and
/// per-day buckets and cannot answer a trailing-window query.
/// </summary>
public sealed class RecentActivityBuilder(SessionFileCache sessionFileCache, CostCalculator costCalculator)
{
    public RecentActivitySummary Build(IReadOnlyList<string> sessionFiles, TimeSpan window)
    {
        var now = DateTimeOffset.Now;
        var since = now - window;

        var messages = 0;
        var toolCalls = 0;
        long totalTokens = 0;
        var sessionIds = new HashSet<string>();
        var tokensByModel = new Dictionary<string, long>();
        var modelUsage = new Dictionary<string, (long Input, long Output, long CacheRead, long CacheCreation)>();
        var hourBuckets = new Dictionary<DateTimeOffset, (int Messages, long Tokens)>();

        foreach (var file in sessionFiles)
        {
            List<Models.SessionMessage> parsed;
            try
            {
                parsed = sessionFileCache.GetRows(file);
            }
            catch
            {
                continue;
            }

            string? sessionId = null;

            foreach (var msg in parsed)
            {
                if (msg.Timestamp is null || !DateTimeOffset.TryParse(msg.Timestamp, out var ts) || ts < since)
                {
                    continue;
                }

                sessionId ??= parsed.FirstOrDefault(m => m.SessionId is not null)?.SessionId
                    ?? Path.GetFileNameWithoutExtension(file);

                if (msg.Type is not "user" and not "assistant")
                {
                    continue;
                }

                sessionIds.Add(sessionId);
                messages++;
                toolCalls += SessionMessageAnalysis.CountToolCalls(msg);

                var hourStart = new DateTimeOffset(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, ts.Offset);
                var bucket = hourBuckets.GetValueOrDefault(hourStart);
                bucket.Messages++;

                var usage = msg.Message?.Usage;
                if (msg.Type == "assistant" && usage is not null)
                {
                    var model = msg.Message?.Model ?? "unknown";
                    if (model != "<synthetic>")
                    {
                        var tokens = usage.InputTokens + usage.OutputTokens
                            + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
                        totalTokens += tokens;
                        tokensByModel[model] = tokensByModel.GetValueOrDefault(model) + tokens;
                        bucket.Tokens += tokens;

                        var entry = modelUsage.GetValueOrDefault(model);
                        modelUsage[model] = (
                            entry.Input + usage.InputTokens,
                            entry.Output + usage.OutputTokens,
                            entry.CacheRead + usage.CacheReadInputTokens,
                            entry.CacheCreation + usage.CacheCreationInputTokens);
                    }
                }

                hourBuckets[hourStart] = bucket;
            }
        }

        var estimatedCost = modelUsage.Sum(kvp =>
            costCalculator.EstimateCost(kvp.Key, kvp.Value.Input, kvp.Value.Output, kvp.Value.CacheRead, kvp.Value.CacheCreation));

        var firstHour = new DateTimeOffset(since.Year, since.Month, since.Day, since.Hour, 0, 0, since.Offset);
        var lastHour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, now.Offset);
        var hourlyTrend = new List<HourBucket>();
        for (var hour = firstHour; hour <= lastHour; hour = hour.AddHours(1))
        {
            var bucket = hourBuckets.GetValueOrDefault(hour);
            hourlyTrend.Add(new(hour, bucket.Messages, bucket.Tokens));
        }

        return new(
            window,
            messages,
            sessionIds.Count,
            toolCalls,
            totalTokens,
            tokensByModel,
            estimatedCost,
            hourlyTrend);
    }
}
