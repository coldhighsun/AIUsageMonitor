using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Aggregates raw session transcripts into a token-by-hour-of-day distribution, since
/// <see cref="Models.StatsCache"/> only tracks message counts per hour, not tokens.
/// </summary>
public sealed class HourlyActivityBuilder(SessionFileCache sessionFileCache)
{
    public List<HourlyActivity> Build(IReadOnlyList<string> sessionFiles)
    {
        var tokensByHour = new long[24];

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

            foreach (var msg in parsed)
            {
                if (msg.Type != "assistant" || msg.Timestamp is null
                    || !DateTimeOffset.TryParse(msg.Timestamp, out var ts))
                {
                    continue;
                }

                var usage = msg.Message?.Usage;
                if (usage is null)
                {
                    continue;
                }

                var model = msg.Message?.Model ?? "unknown";
                if (model == "<synthetic>")
                {
                    continue;
                }

                var tokens = usage.InputTokens + usage.OutputTokens
                    + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
                tokensByHour[ts.LocalDateTime.Hour] += tokens;
            }
        }

        return Enumerable.Range(0, 24)
            .Select(h => new HourlyActivity(h, tokensByHour[h]))
            .ToList();
    }
}
