using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Approximates Claude Pro/Max's rate-limit windows (the rolling 5-hour session window and the
/// weekly window) from local session transcripts, since neither the real reset anchor nor the
/// account's quota lives in any local Claude Code file. When no anchor is supplied, the current
/// session block is derived using a ccusage-style rolling window: messages across all projects
/// are sorted chronologically and grouped into blocks that start at the first message after
/// either no prior block, or a &gt;5 hour idle gap, or the prior block having run its full 5 hours.
/// </summary>
/// <param name="sessionFileCache">The session file cache used to retrieve session messages from session files.</param>
/// <param name="costCalculator">The cost calculator used to estimate costs based on token usage.</param>
public sealed class SessionBlockBuilder(SessionFileCache sessionFileCache, CostCalculator costCalculator)
{
    private static readonly TimeSpan SessionWindowDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekWindowDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// Builds a summary of the current 5-hour session window.
    /// </summary>
    /// <param name="sessionFiles">A list of session file paths to process.</param>
    /// <param name="anchor">
    /// The real window start time, if known (e.g. read from Claude's own UI). When supplied, the
    /// window is pinned to <paramref name="anchor"/>..<paramref name="anchor"/>+5h instead of being
    /// estimated.
    /// </param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A <see cref="UsageWindowSummary"/> describing the current session window.</returns>
    public UsageWindowSummary BuildCurrentSessionWindow(
        IReadOnlyList<string> sessionFiles, DateTimeOffset? anchor, IProgress<int>? progress = null)
    {
        var messages = ReadMessages(sessionFiles, progress);
        var now = DateTimeOffset.Now;

        if (anchor is { } rawAnchor)
        {
            var pinnedStart = RollForwardToCurrentPeriod(rawAnchor, now, SessionWindowDuration);
            var pinnedEnd = pinnedStart + SessionWindowDuration;
            return Summarize(messages.Where(m => m.Timestamp >= pinnedStart && m.Timestamp < pinnedEnd),
                pinnedStart, pinnedEnd, isAnchorEstimated: false);
        }

        DateTimeOffset? blockStart = null;
        DateTimeOffset? blockLast = null;

        foreach (var msg in messages.OrderBy(m => m.Timestamp))
        {
            if (blockStart is null
                || msg.Timestamp - blockLast!.Value > SessionWindowDuration
                || msg.Timestamp - blockStart.Value >= SessionWindowDuration)
            {
                blockStart = msg.Timestamp;
            }

            blockLast = msg.Timestamp;
        }

        if (blockStart is null || now - blockLast!.Value > SessionWindowDuration)
        {
            return new(now, now + SessionWindowDuration, 0, 0, [], 0m, IsAnchorEstimated: true);
        }

        var windowStart = blockStart.Value;
        var resetsAt = windowStart + SessionWindowDuration;
        return Summarize(messages.Where(m => m.Timestamp >= windowStart && m.Timestamp < resetsAt),
            windowStart, resetsAt, isAnchorEstimated: true);
    }

    /// <summary>
    /// Builds a summary of the current weekly window.
    /// </summary>
    /// <param name="sessionFiles">A list of session file paths to process.</param>
    /// <param name="anchor">
    /// The real weekly reset day and local time-of-day, if known (e.g. read from Claude's own UI).
    /// When supplied, the window is pinned to the most recent occurrence of that anchor through
    /// 7 days later instead of being estimated.
    /// </param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A <see cref="UsageWindowSummary"/> describing the current weekly window.</returns>
    public UsageWindowSummary BuildWeekWindow(
        IReadOnlyList<string> sessionFiles, (DayOfWeek Day, TimeSpan TimeOfDay)? anchor, IProgress<int>? progress = null)
    {
        var messages = ReadMessages(sessionFiles, progress);
        var now = DateTimeOffset.Now;

        if (anchor is { } weeklyAnchor)
        {
            var windowStart = MostRecentAnchorOccurrence(now, weeklyAnchor);
            var resetsAt = windowStart + WeekWindowDuration;
            return Summarize(messages.Where(m => m.Timestamp >= windowStart && m.Timestamp < resetsAt),
                windowStart, resetsAt, isAnchorEstimated: false);
        }

        var since = now - WeekWindowDuration;
        var inWindow = messages.Where(m => m.Timestamp >= since).ToList();

        if (inWindow.Count == 0)
        {
            return new(since, since + WeekWindowDuration, 0, 0, [], 0m, IsAnchorEstimated: true);
        }

        var earliest = inWindow.Min(m => m.Timestamp);
        return Summarize(inWindow, since, earliest + WeekWindowDuration, isAnchorEstimated: true);
    }

    /// <summary>
    /// Rolls a fixed anchor timestamp forward (or backward) by whole multiples of <paramref name="period"/>
    /// so the returned start time is that of the period currently containing <paramref name="now"/>. This
    /// keeps a once-configured anchor (e.g. a session start seen once in Claude's own UI) valid indefinitely,
    /// instead of going stale after the first period elapses.
    /// </summary>
    private static DateTimeOffset RollForwardToCurrentPeriod(DateTimeOffset anchor, DateTimeOffset now, TimeSpan period)
    {
        var periodsElapsed = Math.Floor((now - anchor) / period);
        return anchor + periodsElapsed * period;
    }

    private static DateTimeOffset MostRecentAnchorOccurrence(DateTimeOffset now, (DayOfWeek Day, TimeSpan TimeOfDay) anchor)
    {
        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset) + anchor.TimeOfDay;
        var dayDelta = ((int)now.DayOfWeek - (int)anchor.Day + 7) % 7;
        candidate = candidate.AddDays(-dayDelta);
        if (candidate > now)
        {
            candidate = candidate.AddDays(-7);
        }
        return candidate;
    }

    private List<Message> ReadMessages(IReadOnlyList<string> sessionFiles, IProgress<int>? progress)
    {
        var result = new List<Message>();

        for (var fileIndex = 0; fileIndex < sessionFiles.Count; fileIndex++)
        {
            try
            {
                ProcessFile(sessionFiles[fileIndex], result);
            }
            finally
            {
                progress?.Report((fileIndex + 1) * 100 / sessionFiles.Count);
            }
        }

        return result;
    }

    private void ProcessFile(string file, List<Message> result)
    {
        IReadOnlyList<SessionMessage> parsed;
        try
        {
            parsed = sessionFileCache.GetRows(file);
        }
        catch
        {
            return;
        }

        foreach (var msg in parsed)
        {
            if (msg.Type is not "user" and not "assistant"
                || msg.Timestamp is null
                || !DateTimeOffset.TryParse(msg.Timestamp, out var ts))
            {
                continue;
            }

            result.Add(new(ts, msg));
        }
    }

    private UsageWindowSummary Summarize(
        IEnumerable<Message> messages, DateTimeOffset windowStart, DateTimeOffset resetsAt, bool isAnchorEstimated)
    {
        var messageList = messages.ToList();
        var messageCount = 0;
        long totalTokens = 0;
        var tokensByModel = new Dictionary<string, long>();
        var modelUsage = new Dictionary<string, (long Input, long Output, long CacheRead, long CacheCreation5m, long CacheCreation1h)>();
        var seenAssistantMessageIds = new HashSet<(string?, string?)>();

        foreach (var (_, msg) in messageList)
        {
            messageCount++;

            var usage = msg.Message?.Usage;
            if (msg.Type != "assistant" || usage is null)
            {
                continue;
            }

            var model = msg.Message?.Model ?? "unknown";
            var messageKey = (msg.Message!.Id, msg.RequestId) is (null, null)
                ? (msg.Uuid, (string?)null)
                : (msg.Message!.Id, msg.RequestId);
            if (model == "<synthetic>" || !seenAssistantMessageIds.Add(messageKey))
            {
                continue;
            }

            var tokens = usage.InputTokens + usage.OutputTokens
                                            + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
            totalTokens += tokens;
            tokensByModel[model] = tokensByModel.GetValueOrDefault(model) + tokens;

            var (cacheCreation5m, cacheCreation1h) = usage.CacheCreation is { } detail
                ? (detail.Ephemeral5mInputTokens, detail.Ephemeral1hInputTokens)
                : (usage.CacheCreationInputTokens, 0L);

            var entry = modelUsage.GetValueOrDefault(model);
            modelUsage[model] = (
                entry.Input + usage.InputTokens,
                entry.Output + usage.OutputTokens,
                entry.CacheRead + usage.CacheReadInputTokens,
                entry.CacheCreation5m + cacheCreation5m,
                entry.CacheCreation1h + cacheCreation1h);
        }

        var estimatedCost = modelUsage.Sum(kvp =>
            costCalculator.EstimateCost(kvp.Key, kvp.Value.Input, kvp.Value.Output, kvp.Value.CacheRead,
                kvp.Value.CacheCreation5m, kvp.Value.CacheCreation1h));

        return new(windowStart, resetsAt, messageCount, totalTokens, tokensByModel, estimatedCost, isAnchorEstimated);
    }

    private readonly record struct Message(DateTimeOffset Timestamp, SessionMessage Data);
}
