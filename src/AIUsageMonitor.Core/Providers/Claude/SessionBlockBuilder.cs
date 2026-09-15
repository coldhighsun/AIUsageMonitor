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
/// A supplied reset time takes precedence while it is still in the future, since the user copied
/// it out of Claude's own UI. It is deliberately not projected past its own window: once it
/// elapses it says nothing about the next window, because the next window only opens on the first
/// message after it - and that message may well be sent from another device this machine cannot
/// see. With no reset time in effect and no local activity in the last 5 hours, the window is
/// reported as <see cref="WindowConfidence.Unknown"/> rather than guessed.
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
    /// <param name="sessionResetAt">
    /// The real reset time of the current window, if known (e.g. read from Claude's own UI). While
    /// it is still in the future the window is pinned to the 5 hours ending at it; once it has
    /// elapsed it is ignored rather than projected forward.
    /// </param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A <see cref="UsageWindowSummary"/> describing the current session window.</returns>
    public UsageWindowSummary BuildCurrentSessionWindow(
        IReadOnlyList<string> sessionFiles, DateTimeOffset? sessionResetAt, IProgress<int>? progress = null)
    {
        var messages = ReadMessages(sessionFiles, progress);
        var now = DateTimeOffset.Now;

        if (sessionResetAt is { } resetAt && now < resetAt)
        {
            var confirmedStart = resetAt - SessionWindowDuration;
            return Summarize(messages.Where(m => m.Timestamp >= confirmedStart && m.Timestamp < resetAt),
                confirmedStart, resetAt, WindowConfidence.Confirmed);
        }

        var (scanStart, scanLast) = ScanBlocks(messages);

        if (scanStart is null || now - scanLast!.Value > SessionWindowDuration)
        {
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }

        var estimatedResetsAt = scanStart.Value + SessionWindowDuration;
        return Summarize(messages.Where(m => m.Timestamp >= scanStart.Value && m.Timestamp < estimatedResetsAt),
            scanStart.Value, estimatedResetsAt, WindowConfidence.Estimated);
    }

    /// <summary>
    /// Groups messages into ccusage-style blocks (split on either a &gt;5 hour idle gap or the
    /// prior block having run its full 5 hours) and returns the start and last-activity time of
    /// the latest block, or <c>null</c> for both if there are no messages.
    /// </summary>
    private static (DateTimeOffset? Start, DateTimeOffset? Last) ScanBlocks(List<Message> messages)
    {
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

        return (blockStart, blockLast);
    }

    /// <summary>
    /// Builds a summary of the current weekly window.
    /// </summary>
    /// <param name="sessionFiles">A list of session file paths to process.</param>
    /// <param name="anchor">
    /// The real weekly reset day and local time-of-day, if known (e.g. read from Claude's own UI).
    /// This is a fixed weekly schedule assigned to the account (unlike the 5-hour session, it does
    /// not depend on activity and never goes stale), so when supplied the window is always pinned
    /// to the most recent occurrence of it through 7 days later. Without it there is no way to
    /// derive the real reset instant locally, so the window is reported as unknown; only the
    /// trailing 7 days of usage - an upper bound on the current cycle's usage - is shown.
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
                windowStart, resetsAt, WindowConfidence.Confirmed);
        }

        var since = now - WeekWindowDuration;
        var inWindow = messages.Where(m => m.Timestamp >= since);
        return Summarize(inWindow, since, resetsAt: null, WindowConfidence.Unknown);
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
        IEnumerable<Message> messages, DateTimeOffset windowStart, DateTimeOffset? resetsAt, WindowConfidence confidence)
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

        return new(windowStart, resetsAt, messageCount, totalTokens, tokensByModel, estimatedCost, confidence);
    }

    private readonly record struct Message(DateTimeOffset Timestamp, SessionMessage Data);
}
