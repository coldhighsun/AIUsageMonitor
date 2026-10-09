using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Builds a <see cref="StatsCache"/> by aggregating the raw session transcripts under
/// projects/*.jsonl, for use when Claude Code hasn't written (or has removed) its own
/// stats-cache.json.
/// </summary>
/// <param name="sessionFileCache">The session file cache used to retrieve and prune session files.</param>
public sealed class StatsCacheBuilder(SessionFileCache sessionFileCache)
{
    /// <summary>
    /// Builds a <see cref="StatsCache"/> by aggregating the raw session transcripts under
    /// </summary>
    /// <param name="sessionFiles">The list of session files to process.</param>
    /// <param name="progress">An optional progress reporter to report the progress of processing the session files.</param>
    /// <returns>A <see cref="StatsCache"/> object containing the aggregated statistics.</returns>
    public StatsCache Build(IReadOnlyList<string> sessionFiles, IProgress<int>? progress = null)
    {
        sessionFileCache.Prune(sessionFiles);

        var dailyActivity = new Dictionary<DateOnly, (int Messages, HashSet<string> Sessions, int ToolCalls)>();
        var dailyModelTokens = new Dictionary<DateOnly, Dictionary<string, long>>();
        var modelUsage = new Dictionary<string, ModelUsageEntry>();
        var hourCounts = new Dictionary<string, int>();
        var sessionIds = new HashSet<string>();
        DateTimeOffset? firstSessionDate = null;

        // First and last line and message count of each session, over its lines counted once across all files, so a
        // resumed transcript neither lends its copied history to the new session nor takes it from the original.
        var sessionSpans = new Dictionary<string, (DateTimeOffset Start, DateTimeOffset End, int Messages)>();

        var deduplicator = new TranscriptDeduplicator();

        sessionFileCache.WarmUp(sessionFiles, progress);

        foreach (var file in sessionFiles)
        {
            ProcessFile(file);
        }

        void ProcessFile(string file)
        {
            IReadOnlyList<SessionMessage> messages;
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

            // Only a fallback for lines without their own session id: a resumed transcript begins with lines copied
            // from the original session, so the first line's id says nothing about the session the new lines belong to.
            var fileSessionId = messages.FirstOrDefault(m => m.SessionId is not null)?.SessionId
                ?? Path.GetFileNameWithoutExtension(file);

            foreach (var msg in messages)
            {
                if (msg.Timestamp is not { } ts)
                {
                    continue;
                }

                // A line copied in from the transcript this session was resumed from was already counted there.
                if (!deduplicator.TryAddLine(msg))
                {
                    continue;
                }

                var sessionId = msg.SessionId ?? fileSessionId;
                var isMessage = msg.Type is "user" or "assistant";
                sessionSpans[sessionId] = sessionSpans.TryGetValue(sessionId, out var span)
                    ? (ts < span.Start ? ts : span.Start, ts > span.End ? ts : span.End, span.Messages + (isMessage ? 1 : 0))
                    : (ts, ts, isMessage ? 1 : 0);

                var dateOnly = DateOnly.FromDateTime(ts.LocalDateTime);
                if (firstSessionDate is null || ts < firstSessionDate)
                {
                    firstSessionDate = ts;
                }

                if (!isMessage)
                {
                    continue;
                }

                sessionIds.Add(sessionId);

                var bucket = dailyActivity.TryGetValue(dateOnly, out var existing)
                    ? existing
                    : (Messages: 0, Sessions: new HashSet<string>(), ToolCalls: 0);
                bucket.Sessions.Add(sessionId);
                bucket.Messages++;
                bucket.ToolCalls += msg.Message?.ToolUseCount ?? 0;
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

                    if (!deduplicator.TryAddUsage(msg))
                    {
                        continue;
                    }

                    var tokens = usage.TotalTokens;

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
        }

        string? longestSessionId = null;
        long longestDurationMs = 0;
        var longestMessageCount = 0;
        string? longestTimestamp = null;
        foreach (var (id, span) in sessionSpans)
        {
            // A session seen only on non-message lines has no activity and is not in TotalSessions either.
            var durationMs = (long)(span.End - span.Start).TotalMilliseconds;
            if (span.Messages > 0 && durationMs > longestDurationMs)
            {
                longestDurationMs = durationMs;
                longestSessionId = id;
                longestMessageCount = span.Messages;
                longestTimestamp = span.Start.ToString("O");
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