using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Aggregates raw session transcripts into a rolling "last N hours" activity
/// summary, since <see cref="Models.StatsCache"/> only tracks hour-of-day and
/// per-day buckets and cannot answer a trailing-window query.
/// </summary>
/// <param name="costCalculator">The cost calculator used to estimate costs based on token usage.</param>
/// <param name="sessionFileCache">The session file cache used to retrieve session messages from session files.</param>
/// <param name="timeProvider">The clock and local time zone used to place the hourly buckets; defaults to the system clock.</param>
public sealed class RecentActivityBuilder(
    SessionFileCache sessionFileCache, CostCalculator costCalculator, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The clock and local time zone used to place the hourly buckets.
    /// </summary>
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Builds a recent activity summary for the specified session files within the given time window.
    /// </summary>
    /// <param name="sessionFiles">A list of session file paths to process.</param>
    /// <param name="window">The time window for which to build the recent activity summary.</param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A <see cref="RecentActivitySummary"/> object representing the recent activity.</returns>
    public RecentActivitySummary Build(IReadOnlyList<string> sessionFiles, TimeSpan window, IProgress<int>? progress = null)
    {
        var zone = _clock.LocalTimeZone;
        var now = _clock.GetLocalNow();
        var since = now - window;

        var messages = 0;
        var toolCalls = 0;
        long totalTokens = 0;
        var sessionIds = new HashSet<string>();
        var tokensByModel = new Dictionary<string, long>();
        var modelUsage = new Dictionary<string, ModelTokenTotals>();
        var hourBuckets = new Dictionary<DateTimeOffset, (int Messages, long Tokens)>();
        var deduplicator = new TranscriptDeduplicator();

        var candidateFiles = sessionFileCache.GetFilesModifiedSince(sessionFiles, since);
        sessionFileCache.WarmUp(candidateFiles, progress);

        foreach (var file in candidateFiles)
        {
            ProcessFile(file);
        }

        var estimatedCost = ModelTokenTotals.EstimateCost(modelUsage, costCalculator);

        var firstHour = StartOfLocalHour(since, zone);
        var lastHour = StartOfLocalHour(now, zone);
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

        void ProcessFile(string file)
        {
            IReadOnlyList<Models.SessionMessage> parsed;
            try
            {
                parsed = sessionFileCache.GetRows(file);
            }
            catch
            {
                return;
            }

            // Only a fallback for lines without their own session id; see StatsCacheBuilder for why the first line's id
            // cannot stand for the whole file.
            string? fileSessionId = null;

            foreach (var msg in parsed)
            {
                if (msg.Timestamp is not { } ts || ts < since)
                {
                    continue;
                }

                if (msg.Type is not "user" and not "assistant")
                {
                    continue;
                }

                // A line copied in from the transcript this session was resumed from was already counted there.
                if (!deduplicator.TryAddLine(msg))
                {
                    continue;
                }

                fileSessionId ??= parsed.FirstOrDefault(m => m.SessionId is not null)?.SessionId
                                  ?? Path.GetFileNameWithoutExtension(file);
                sessionIds.Add(msg.SessionId ?? fileSessionId);
                messages++;
                toolCalls += msg.Message?.ToolUseCount ?? 0;

                var hourStart = StartOfLocalHour(ts, zone);
                var bucket = hourBuckets.GetValueOrDefault(hourStart);
                bucket.Messages++;

                var usage = msg.Message?.Usage;
                if (msg.Type == "assistant" && usage is not null)
                {
                    var model = msg.Message?.Model ?? "unknown";
                    if (model != "<synthetic>" && deduplicator.TryAddUsage(msg))
                    {
                        var tokens = ModelTokenTotals.Record(modelUsage, model, usage);
                        totalTokens += tokens;
                        tokensByModel[model] = tokensByModel.GetValueOrDefault(model) + tokens;
                        bucket.Tokens += tokens;
                    }
                }

                hourBuckets[hourStart] = bucket;
            }
        }
    }

    /// <summary>
    /// Finds the start of the local clock hour that contains a moment. Transcript timestamps are in UTC, which differs
    /// from local hour boundaries in time zones with a half-hour or 45-minute offset, so the conversion goes through
    /// the local zone and not through the timestamp's own offset.
    /// </summary>
    /// <param name="moment">The moment to place.</param>
    /// <param name="zone">The local time zone.</param>
    /// <returns>The start of the local hour, with the zone's offset at that time.</returns>
    internal static DateTimeOffset StartOfLocalHour(DateTimeOffset moment, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(moment, zone);

        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset);
    }
}
