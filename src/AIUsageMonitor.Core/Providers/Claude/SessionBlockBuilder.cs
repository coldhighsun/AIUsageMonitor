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
/// <param name="timeProvider">The clock and local time zone used to place the windows; defaults to the system clock.</param>
public sealed class SessionBlockBuilder(
    SessionFileCache sessionFileCache, CostCalculator costCalculator, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The clock and local time zone used to place the windows.
    /// </summary>
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly TimeSpan SessionWindowDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekWindowDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// The granularity an estimated window start is floored to, which bounds how far before a scan start the window may reach.
    /// </summary>
    private static readonly TimeSpan WindowStartFloorGranularity = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The progressively wider lookbacks tried when estimating the current block, before falling back to the whole history.
    /// </summary>
    private static readonly TimeSpan[] BlockLookbacks = [TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(14)];

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
        var now = _clock.GetLocalNow();

        if (sessionResetAt is { } resetAt && now < resetAt)
        {
            var confirmedStart = resetAt - SessionWindowDuration;
            var pinnedMessages = ReadMessages(sessionFiles, progress, confirmedStart);
            return Summarize(pinnedMessages.Where(m => m.Timestamp >= confirmedStart && m.Timestamp < resetAt),
                confirmedStart, resetAt, WindowConfidence.Confirmed);
        }

        // A confirmed reset time that has elapsed is a known boundary: the next window only opens
        // on the first message after it, so pre-reset activity must not bleed into it even if it
        // would otherwise look like the same rolling block (no >5h idle gap).
        List<Message> messages;
        List<Message> scanCandidates;
        if (sessionResetAt is { } elapsedResetAt)
        {
            messages = ReadMessages(sessionFiles, progress, elapsedResetAt - WindowStartFloorGranularity);
            scanCandidates = messages.Where(m => m.Timestamp >= elapsedResetAt).ToList();
        }
        else
        {
            messages = ReadMessagesForBlockScan(sessionFiles, now, progress);
            scanCandidates = messages;
        }

        var (scanStart, _) = ScanBlocks(scanCandidates);

        if (scanStart is null)
        {
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }

        var flooredStart = FloorToTenMinutes(scanStart.Value);
        var estimatedResetsAt = flooredStart + SessionWindowDuration;

        // Judged against the floored reset time that is displayed, so a window is never reported with a reset in the past.
        if (now >= estimatedResetsAt)
        {
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }

        // Flooring may reach back before a known reset, but activity before it belongs to the previous window.
        var firstIncluded = sessionResetAt is { } knownReset && knownReset > flooredStart ? knownReset : flooredStart;
        return Summarize(messages.Where(m => m.Timestamp >= firstIncluded && m.Timestamp < estimatedResetsAt),
            flooredStart, estimatedResetsAt, WindowConfidence.Estimated);
    }

    /// <summary>
    /// Floors a timestamp down to the nearest 10-minute mark (seconds and sub-second components are
    /// dropped), matching the granularity Claude's own UI uses for its reset times. Used only when
    /// estimating a block's start locally, so the derived reset time lands on the same kind of round
    /// clock value (e.g. :30, :40) a user would see if they had a confirmed anchor instead. Session
    /// transcript timestamps are recorded in UTC (offset zero), but the reset time this produces is
    /// compared and displayed against the user's local clock, so the timestamp is converted to local
    /// time before flooring - otherwise the floored (and later +5h) value would land on the wrong
    /// clock hour whenever local time isn't UTC.
    /// </summary>
    /// <param name="timestamp">The timestamp to floor.</param>
    /// <returns>The timestamp floored to the nearest 10-minute mark, in local time.</returns>
    private DateTimeOffset FloorToTenMinutes(DateTimeOffset timestamp)
    {
        var local = TimeZoneInfo.ConvertTime(timestamp, _clock.LocalTimeZone);
        var flooredMinute = local.Minute / 10 * 10;
        return new DateTimeOffset(
            local.Year, local.Month, local.Day, local.Hour, flooredMinute, 0, local.Offset);
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
        var now = _clock.GetLocalNow();

        if (anchor is { } weeklyAnchor)
        {
            var (windowStart, resetsAt) = GetAnchoredWeek(now, weeklyAnchor, _clock.LocalTimeZone);
            var anchoredMessages = ReadMessages(sessionFiles, progress, windowStart);
            return Summarize(anchoredMessages.Where(m => m.Timestamp >= windowStart && m.Timestamp < resetsAt),
                windowStart, resetsAt, WindowConfidence.Confirmed);
        }

        var since = now - WeekWindowDuration;
        var inWindow = ReadMessages(sessionFiles, progress, since).Where(m => m.Timestamp >= since);
        return Summarize(inWindow, since, resetsAt: null, WindowConfidence.Unknown);
    }

    /// <summary>
    /// Finds the weekly window that contains <paramref name="now"/> when the week resets at a fixed local day and
    /// time. The start and the reset are both placed on that local wall-clock time, so a daylight-saving change
    /// inside the week does not shift either of them by an hour.
    /// </summary>
    /// <remarks>
    /// The wall-clock arithmetic uses <see cref="DateTime"/> (kind unspecified) on purpose: a local time without an
    /// offset cannot be a <see cref="DateTimeOffset"/>, and <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> is what
    /// supplies the offset. Every value that leaves the method is a <see cref="DateTimeOffset"/>.
    /// </remarks>
    /// <param name="now">The current time.</param>
    /// <param name="anchor">The local day of week and time of day at which the week resets.</param>
    /// <param name="zone">The time zone the anchor is expressed in.</param>
    /// <returns>The start of the current window and the moment it resets.</returns>
    internal static (DateTimeOffset Start, DateTimeOffset ResetsAt) GetAnchoredWeek(
        DateTimeOffset now, (DayOfWeek Day, TimeSpan TimeOfDay) anchor, TimeZoneInfo zone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var dayDelta = ((int)localNow.DayOfWeek - (int)anchor.Day + 7) % 7;
        var startLocal = localNow.Date.AddDays(-dayDelta) + anchor.TimeOfDay;
        var start = ToZoneOffset(startLocal, zone);
        if (start > now)
        {
            startLocal = startLocal.AddDays(-7);
            start = ToZoneOffset(startLocal, zone);
        }

        return (start, ToZoneOffset(startLocal.AddDays(7), zone));
    }

    /// <summary>
    /// Attaches the UTC offset that <paramref name="zone"/> has at a local wall-clock time.
    /// </summary>
    /// <param name="local">The local wall-clock time.</param>
    /// <param name="zone">The time zone the time is expressed in.</param>
    /// <returns>The same wall-clock time as a <see cref="DateTimeOffset"/>.</returns>
    private static DateTimeOffset ToZoneOffset(DateTime local, TimeZoneInfo zone)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));
    }

    /// <summary>
    /// Reads the user/assistant messages of all files that may hold messages at or after <paramref name="since"/>.
    /// </summary>
    /// <param name="sessionFiles">The session transcript files.</param>
    /// <param name="progress">An optional progress reporter (0-100).</param>
    /// <param name="since">The earliest message timestamp needed, or <see langword="null"/> to read the whole history.</param>
    /// <returns>All messages of the selected files; may include some older than <paramref name="since"/>.</returns>
    private List<Message> ReadMessages(
        IReadOnlyList<string> sessionFiles, IProgress<int>? progress, DateTimeOffset? since = null)
    {
        var result = new List<Message>();
        var candidates = since is { } start ? sessionFileCache.GetFilesModifiedSince(sessionFiles, start) : sessionFiles;

        sessionFileCache.WarmUp(candidates, progress);

        foreach (var file in candidates)
        {
            ProcessFile(file, result);
        }

        return result;
    }

    /// <summary>
    /// Reads enough recent history to derive the latest rolling block exactly. Block boundaries depend on the
    /// phase of the whole chain of earlier blocks, but a gap longer than the window always starts a new block no
    /// matter what came before it. So the lookback is widened until the loaded messages contain such a gap (or the
    /// lookback itself begins with one); only then is the result guaranteed to equal a scan of the full history.
    /// </summary>
    /// <param name="sessionFiles">The session transcript files.</param>
    /// <param name="now">The current time.</param>
    /// <param name="progress">An optional progress reporter (0-100), used only for the final whole-history read.</param>
    /// <returns>Messages sufficient to scan for the latest block.</returns>
    private List<Message> ReadMessagesForBlockScan(
        IReadOnlyList<string> sessionFiles, DateTimeOffset now, IProgress<int>? progress)
    {
        foreach (var lookback in BlockLookbacks)
        {
            var cutoff = now - lookback;
            var messages = ReadMessages(sessionFiles, null, cutoff);
            if (HasBlockAnchor(messages, cutoff))
            {
                return messages;
            }
        }

        return ReadMessages(sessionFiles, progress);
    }

    /// <summary>
    /// Determines whether scanning <paramref name="messages"/> alone yields the same latest block as scanning all
    /// earlier history too. That holds when a gap longer than the window separates two consecutive messages, or
    /// separates the lookback cutoff from the first message at or after it, or when nothing was recorded since
    /// the cutoff at all (then the latest block started well over a window ago either way).
    /// </summary>
    /// <param name="messages">The loaded messages, in any order.</param>
    /// <param name="cutoff">The lookback cutoff; every message at or after it is guaranteed to be loaded.</param>
    /// <returns><see langword="true"/> if the loaded messages are sufficient for the block scan.</returns>
    private static bool HasBlockAnchor(List<Message> messages, DateTimeOffset cutoff)
    {
        var ordered = messages.Where(m => m.Timestamp >= cutoff).OrderBy(m => m.Timestamp).ToList();
        if (ordered.Count == 0 || ordered[0].Timestamp - cutoff > SessionWindowDuration)
        {
            return true;
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Timestamp - ordered[i - 1].Timestamp > SessionWindowDuration)
            {
                return true;
            }
        }

        return false;
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
                || msg.Timestamp is not { } ts)
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
        var modelUsage = new Dictionary<string, ModelTokenTotals>();
        var deduplicator = new TranscriptDeduplicator();

        foreach (var (_, msg) in messageList)
        {
            // A line copied in from the transcript this session was resumed from was already counted there.
            if (!deduplicator.TryAddLine(msg))
            {
                continue;
            }

            messageCount++;

            var usage = msg.Message?.Usage;
            if (msg.Type != "assistant" || usage is null)
            {
                continue;
            }

            var model = msg.Message?.Model ?? "unknown";
            if (model == "<synthetic>" || !deduplicator.TryAddUsage(msg))
            {
                continue;
            }

            var tokens = ModelTokenTotals.Record(modelUsage, model, usage);
            totalTokens += tokens;
            tokensByModel[model] = tokensByModel.GetValueOrDefault(model) + tokens;
        }

        var estimatedCost = ModelTokenTotals.EstimateCost(modelUsage, costCalculator);

        return new(windowStart, resetsAt, messageCount, totalTokens, tokensByModel, estimatedCost, confidence);
    }

    private readonly record struct Message(DateTimeOffset Timestamp, SessionMessage Data);
}
