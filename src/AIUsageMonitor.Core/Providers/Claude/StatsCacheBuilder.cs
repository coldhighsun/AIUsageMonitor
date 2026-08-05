using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Builds a <see cref="StatsCache"/> by aggregating the raw session transcripts under
/// projects/*.jsonl, for use when Claude Code hasn't written (or has removed) its own
/// stats-cache.json.
/// </summary>
public sealed class StatsCacheBuilder(SessionFileCache sessionFileCache)
{
    public StatsCache Build(IReadOnlyList<string> sessionFiles, IProgress<int>? progress = null)
    {
        sessionFileCache.Prune(sessionFiles);

        var dailyActivity = new Dictionary<DateOnly, (int Messages, HashSet<string> Sessions, int ToolCalls)>();
        var dailyModelTokens = new Dictionary<DateOnly, Dictionary<string, long>>();
        var modelUsage = new Dictionary<string, ModelUsageEntry>();
        var hourCounts = new Dictionary<string, int>();
        var sessionIds = new HashSet<string>();
        DateTimeOffset? firstSessionDate = null;
        string? longestSessionId = null;
        long longestDurationMs = 0;
        var longestMessageCount = 0;
        string? longestTimestamp = null;

        for (var fileIndex = 0; fileIndex < sessionFiles.Count; fileIndex++)
        {
            try
            {
                ProcessFile(sessionFiles[fileIndex]);
            }
            finally
            {
                progress?.Report((fileIndex + 1) * 100 / sessionFiles.Count);
            }
        }

        void ProcessFile(string file)
        {
            List<SessionMessage> messages;
            try
            {
                messages = sessionFileCache.GetRows(file);
            }
            catch
            {
                return;
            }

            if (messages.Count == 0)
            {
                return;
            }

            var sessionId = messages.FirstOrDefault(m => m.SessionId is not null)?.SessionId
                ?? Path.GetFileNameWithoutExtension(file);
            sessionIds.Add(sessionId);

            DateTimeOffset? sessionStart = null;
            DateTimeOffset? sessionEnd = null;

            foreach (var msg in messages)
            {
                if (msg.Timestamp is null || !DateTimeOffset.TryParse(msg.Timestamp, out var ts))
                {
                    continue;
                }

                if (sessionStart is null || ts < sessionStart) sessionStart = ts;
                if (sessionEnd is null || ts > sessionEnd) sessionEnd = ts;

                var dateOnly = DateOnly.FromDateTime(ts.LocalDateTime);
                if (firstSessionDate is null || ts < firstSessionDate)
                {
                    firstSessionDate = ts;
                }

                if (msg.Type is not "user" and not "assistant")
                {
                    continue;
                }

                var bucket = dailyActivity.TryGetValue(dateOnly, out var existing)
                    ? existing
                    : (Messages: 0, Sessions: new HashSet<string>(), ToolCalls: 0);
                bucket.Sessions.Add(sessionId);
                bucket.Messages++;
                bucket.ToolCalls += SessionMessageAnalysis.CountToolCalls(msg);
                dailyActivity[dateOnly] = bucket;

                var hourKey = ts.LocalDateTime.Hour.ToString();
                hourCounts[hourKey] = hourCounts.GetValueOrDefault(hourKey) + 1;

                var usage = msg.Message?.Usage;
                if (msg.Type == "assistant" && usage is not null)
                {
                    var model = msg.Message?.Model ?? "unknown";
                    if (model == "<synthetic>")
                    {
                        continue;
                    }

                    var tokens = usage.InputTokens + usage.OutputTokens
                        + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;

                    var modelTokensForDate = dailyModelTokens.TryGetValue(dateOnly, out var mt)
                        ? mt
                        : dailyModelTokens[dateOnly] = [];
                    modelTokensForDate[model] = modelTokensForDate.GetValueOrDefault(model) + tokens;

                    if (!modelUsage.TryGetValue(model, out var entry))
                    {
                        entry = new();
                    }

                    modelUsage[model] = new()
                    {
                        InputTokens = entry.InputTokens + usage.InputTokens,
                        OutputTokens = entry.OutputTokens + usage.OutputTokens,
                        CacheReadInputTokens = entry.CacheReadInputTokens + usage.CacheReadInputTokens,
                        CacheCreationInputTokens = entry.CacheCreationInputTokens + usage.CacheCreationInputTokens,
                        ContextWindow = entry.ContextWindow,
                        MaxOutputTokens = entry.MaxOutputTokens,
                        WebSearchRequests = entry.WebSearchRequests,
                        CostUSD = entry.CostUSD,
                    };
                }
            }

            if (sessionStart is not null && sessionEnd is not null)
            {
                var durationMs = (long)(sessionEnd.Value - sessionStart.Value).TotalMilliseconds;
                if (durationMs > longestDurationMs)
                {
                    longestDurationMs = durationMs;
                    longestSessionId = sessionId;
                    longestMessageCount = messages.Count(m => m.Type is "user" or "assistant");
                    longestTimestamp = sessionStart.Value.ToString("O");
                }
            }
        }

        return new()
        {
            DailyActivity = dailyActivity
                .Select(kvp => new DailyActivity
                {
                    Date = kvp.Key,
                    MessageCount = kvp.Value.Messages,
                    SessionCount = kvp.Value.Sessions.Count,
                    ToolCallCount = kvp.Value.ToolCalls,
                })
                .OrderBy(a => a.Date)
                .ToList(),
            DailyModelTokens = dailyModelTokens
                .Select(kvp => new DailyModelTokens { Date = kvp.Key, TokensByModel = kvp.Value })
                .OrderBy(t => t.Date)
                .ToList(),
            FirstSessionDate = firstSessionDate,
            HourCounts = hourCounts,
            LastComputedDate = DateOnly.FromDateTime(DateTime.UtcNow),
            LongestSession = longestSessionId is not null
                ? new()
                {
                    Duration = longestDurationMs,
                    MessageCount = longestMessageCount,
                    SessionId = longestSessionId,
                    Timestamp = longestTimestamp ?? "",
                }
                : null,
            ModelUsage = modelUsage,
            TotalMessages = dailyActivity.Values.Sum(v => v.Messages),
            TotalSessions = sessionIds.Count,
            Version = 0,
        };
    }
}