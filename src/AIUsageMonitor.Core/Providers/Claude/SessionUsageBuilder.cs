using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Aggregates raw session transcripts into per-session usage for a range of local days. A session is identified by the
/// session id on each line, so one resumed across several transcript files is still one session, and lines copied into
/// a resumed transcript are counted once, as in the other transcript aggregations.
/// </summary>
/// <param name="sessionFileCache">The session file cache used to retrieve session messages from session files.</param>
/// <param name="costCalculator">The cost calculator used to estimate costs based on token usage.</param>
/// <param name="timeProvider">The clock and local time zone used to place lines on local days; defaults to the system clock.</param>
public sealed class SessionUsageBuilder(
    SessionFileCache sessionFileCache, CostCalculator costCalculator, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The earliest moment a range is widened to cover; no transcript is older.
    /// </summary>
    private static readonly DateTimeOffset EarliestBound = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The latest moment a range is widened to cover; no transcript is newer.
    /// </summary>
    private static readonly DateTimeOffset LatestBound = new(9999, 12, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The clock whose local time zone decides which calendar day a line belongs to, read on every build.
    /// </summary>
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Builds per-session usage for the specified session files and range of local days.
    /// </summary>
    /// <param name="sessionFiles">The session file paths to process.</param>
    /// <param name="projectsDir">The directory the session files live under; the first folder below it names the project.</param>
    /// <param name="from">The first local day to include.</param>
    /// <param name="to">The last local day to include.</param>
    /// <param name="modelFilter">
    /// When set, only assistant lines of models whose name contains this text (case-insensitive) are counted, for
    /// messages, tool calls, tokens and cost alike.
    /// </param>
    /// <param name="progress">An optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>
    /// One entry per session with at least one counted line, ordered by start time. A session that began before
    /// <paramref name="from"/> shows only what happened inside the range.
    /// </returns>
    public List<SessionUsage> Build(
        IReadOnlyList<string> sessionFiles,
        string projectsDir,
        DateOnly from,
        DateOnly to,
        string? modelFilter = null,
        IProgress<int>? progress = null)
    {
        var zone = _clock.LocalTimeZone;

        // Local days map to UTC instants within +-14 hours of midnight UTC, so a day margin on each side never drops a line
        // that belongs to the range; the exact local-day check below decides.
        var coarseFrom = CoarseBound(from, -1);
        var coarseTo = CoarseBound(to, 2);
        var model = string.IsNullOrWhiteSpace(modelFilter) ? null : modelFilter.Trim();

        var candidateFiles = sessionFileCache.GetFilesModifiedSince(sessionFiles, coarseFrom)
            .Order(StringComparer.Ordinal)
            .ToList();
        sessionFileCache.WarmUp(candidateFiles, progress);
        if (candidateFiles.Count == 0)
        {
            progress?.Report(100);
        }

        var transcripts = candidateFiles
            .Select(file => Partition(file, projectsDir, coarseFrom, coarseTo))
            .OfType<TranscriptLines>()
            .ToList();

        var deduplicator = new TranscriptDeduplicator();
        var sessions = new Dictionary<string, SessionAccumulator>();

        // Claude Code names a transcript after its session, so a line sits in its home transcript when its session id matches
        // the file name. Home lines are counted first: when a resumed transcript in another folder copies them, the copy is
        // the duplicate, and the original's folder is the one the session is credited to, whatever the files' times or names.
        foreach (var transcript in transcripts)
        {
            Count(transcript, transcript.HomeLines);
        }

        foreach (var transcript in transcripts)
        {
            Count(transcript, transcript.OtherLines);
        }

        return sessions
            .Select(pair => pair.Value.ToUsage(pair.Key, costCalculator))
            .OrderBy(s => s.Start)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();

        void Count(TranscriptLines transcript, List<TimedLine> lines)
        {
            foreach (var (ts, msg) in lines)
            {
                // A line copied in from the transcript this session was resumed from was already counted there.
                if (!deduplicator.TryAddLine(msg))
                {
                    continue;
                }

                // A streamed response is several lines sharing one usage; the first one claims it, wherever it falls, so a
                // response straddling the edge of the range is attributed to the same day as in the daily statistics.
                var usage = msg.Message?.Usage;
                var lineModel = msg.Message?.Model ?? "unknown";
                var claimsUsage = msg.Type == "assistant"
                    && usage is not null
                    && lineModel != "<synthetic>"
                    && deduplicator.TryAddUsage(msg);

                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ts, zone).DateTime);
                if (day < from || day > to)
                {
                    continue;
                }

                if (model is not null
                    && (msg.Type != "assistant" || !lineModel.Contains(model, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var sessionId = msg.SessionId ?? transcript.FallbackSessionId;
                if (!sessions.TryGetValue(sessionId, out var session))
                {
                    session = new SessionAccumulator();
                    sessions[sessionId] = session;
                }

                session.AddLine(ts, transcript.Folder, msg.Cwd, msg.Message?.ToolUseCount ?? 0);
                if (claimsUsage)
                {
                    session.AddUsage(lineModel, usage!);
                }
            }
        }
    }

    /// <summary>
    /// Reads a transcript and sorts its message lines that fall inside the widened range into home lines and other lines.
    /// </summary>
    /// <param name="file">The path of the transcript.</param>
    /// <param name="projectsDir">The directory the session files live under.</param>
    /// <param name="coarseFrom">The earliest moment a line may have.</param>
    /// <param name="coarseTo">The moment from which lines are too late.</param>
    /// <returns>The sorted lines, or <see langword="null"/> when the transcript cannot be read.</returns>
    private TranscriptLines? Partition(string file, string projectsDir, DateTimeOffset coarseFrom, DateTimeOffset coarseTo)
    {
        IReadOnlyList<SessionMessage> parsed;
        try
        {
            parsed = sessionFileCache.GetRows(file);
        }
        catch
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(file);
        var home = new List<TimedLine>();
        var other = new List<TimedLine>();

        foreach (var msg in parsed)
        {
            if (msg.Timestamp is not { } ts || ts < coarseFrom || ts >= coarseTo)
            {
                continue;
            }

            if (msg.Type is not "user" and not "assistant")
            {
                continue;
            }

            // Lines without a session id take the identity of their file, so they are home lines too.
            (msg.SessionId is null || msg.SessionId == fileName ? home : other).Add(new TimedLine(ts, msg));
        }

        // Only a fallback for lines without their own session id; see StatsCacheBuilder for why the first line's id cannot
        // stand for the whole file.
        var fallbackSessionId = parsed.FirstOrDefault(m => m.SessionId is not null)?.SessionId ?? fileName;
        return new TranscriptLines(ProjectFolder(projectsDir, file), fallbackSessionId, home, other);
    }

    /// <summary>
    /// Gets the start of the UTC day a number of days away from a local day, kept within a range where date arithmetic
    /// cannot overflow, so a range such as <see cref="DateOnly.MinValue"/> to <see cref="DateOnly.MaxValue"/> means everything.
    /// </summary>
    /// <param name="day">The local day.</param>
    /// <param name="marginDays">The days to move, negative for earlier.</param>
    /// <returns>The moment, clamped to January 1970 at the earliest and December 9999 at the latest.</returns>
    private static DateTimeOffset CoarseBound(DateOnly day, int marginDays)
    {
        var number = Math.Clamp((long)day.DayNumber + marginDays, DateOnly.MinValue.DayNumber, DateOnly.MaxValue.DayNumber);
        var start = new DateTimeOffset(DateOnly.FromDayNumber((int)number).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        return start < EarliestBound ? EarliestBound : start > LatestBound ? LatestBound : start;
    }

    /// <summary>
    /// Finds the project folder a transcript belongs to: the first folder below the projects directory, which also covers
    /// transcripts nested deeper, such as those of sub-agents.
    /// </summary>
    /// <param name="projectsDir">The directory the session files live under.</param>
    /// <param name="file">The path of the transcript.</param>
    /// <returns>The name of the project folder.</returns>
    internal static string ProjectFolder(string projectsDir, string file)
    {
        var relative = Path.GetRelativePath(projectsDir, file);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length > 1 && segments[0] != "..")
        {
            return segments[0];
        }

        return Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
    }

    /// <summary>
    /// Compares two (time, folder) positions, earliest first, with the folder name as a tiebreak so the result does not
    /// depend on the order files are visited in.
    /// </summary>
    /// <param name="time">The time of the first position.</param>
    /// <param name="folder">The folder of the first position.</param>
    /// <param name="otherTime">The time of the second position.</param>
    /// <param name="otherFolder">The folder of the second position.</param>
    /// <returns>A negative number when the first position comes first.</returns>
    private static int ComparePosition(DateTimeOffset time, string folder, DateTimeOffset otherTime, string otherFolder)
    {
        var byTime = time.CompareTo(otherTime);
        return byTime != 0 ? byTime : string.CompareOrdinal(folder, otherFolder);
    }

    /// <summary>
    /// A transcript line with its time, already known to be a user or assistant message inside the widened range.
    /// </summary>
    /// <param name="Time">The time of the line.</param>
    /// <param name="Message">The line.</param>
    private readonly record struct TimedLine(DateTimeOffset Time, SessionMessage Message);

    /// <summary>
    /// The usable lines of one transcript, sorted once so each counting pass walks only the lines it needs.
    /// </summary>
    /// <param name="Folder">The project folder of the transcript.</param>
    /// <param name="FallbackSessionId">The session id of lines that have none.</param>
    /// <param name="HomeLines">The lines whose session id is the transcript's own, or that have none.</param>
    /// <param name="OtherLines">The lines copied in from, or belonging to, another session.</param>
    private sealed record TranscriptLines(string Folder, string FallbackSessionId, List<TimedLine> HomeLines, List<TimedLine> OtherLines);

    /// <summary>
    /// Collects the counted lines of one session while the transcripts are walked.
    /// </summary>
    private sealed class SessionAccumulator
    {
        /// <summary>
        /// The usage of each model seen in the session.
        /// </summary>
        private readonly Dictionary<string, ModelTokenTotals> _models = [];

        /// <summary>
        /// The time and folder of the earliest counted line.
        /// </summary>
        private (DateTimeOffset Time, string Folder)? _first;

        /// <summary>
        /// The time and folder of the latest counted line.
        /// </summary>
        private DateTimeOffset _last;

        /// <summary>
        /// The time, folder and working directory of the earliest counted line that has a working directory.
        /// </summary>
        private (DateTimeOffset Time, string Folder, string Cwd)? _firstCwd;

        /// <summary>
        /// The number of counted lines.
        /// </summary>
        private int _messages;

        /// <summary>
        /// The number of tool calls.
        /// </summary>
        private int _toolCalls;

        /// <summary>
        /// Counts a transcript line.
        /// </summary>
        /// <param name="time">The time of the line.</param>
        /// <param name="folder">The project folder of the transcript holding the line.</param>
        /// <param name="cwd">The working directory recorded on the line, if any.</param>
        /// <param name="toolCalls">The number of tool calls on the line.</param>
        public void AddLine(DateTimeOffset time, string folder, string? cwd, int toolCalls)
        {
            _messages++;
            _toolCalls += toolCalls;

            if (_first is not { } first || ComparePosition(time, folder, first.Time, first.Folder) < 0)
            {
                _first = (time, folder);
            }

            if (_messages == 1 || time > _last)
            {
                _last = time;
            }

            if (cwd is not null
                && (_firstCwd is not { } firstCwd || ComparePosition(time, folder, firstCwd.Time, firstCwd.Folder) < 0))
            {
                _firstCwd = (time, folder, cwd);
            }
        }

        /// <summary>
        /// Adds the token usage of a response.
        /// </summary>
        /// <param name="model">The model that produced the response.</param>
        /// <param name="usage">The usage reported for the response.</param>
        public void AddUsage(string model, TokenUsage usage)
        {
            ModelTokenTotals.Record(_models, model, usage);
        }

        /// <summary>
        /// Turns the collected lines into a session record, pricing each model's usage.
        /// </summary>
        /// <param name="sessionId">The identifier of the session.</param>
        /// <param name="costCalculator">The calculator that prices the token usage.</param>
        /// <returns>The usage of the session.</returns>
        public SessionUsage ToUsage(string sessionId, CostCalculator costCalculator)
        {
            var tokensByModel = new Dictionary<string, long>();
            var input = 0L;
            var output = 0L;
            var cacheRead = 0L;
            var cacheWrite = 0L;

            foreach (var (model, totals) in _models)
            {
                tokensByModel[model] = totals.Total;
                input += totals.Input;
                output += totals.Output;
                cacheRead += totals.CacheRead;
                cacheWrite += totals.CacheWrite;
            }

            return new SessionUsage(
                sessionId,
                _first!.Value.Folder,
                _firstCwd?.Cwd,
                _first.Value.Time,
                _last,
                _messages,
                _toolCalls,
                input + output + cacheRead + cacheWrite,
                input,
                output,
                cacheRead,
                cacheWrite,
                tokensByModel,
                ModelTokenTotals.EstimateCost(_models, costCalculator));
        }
    }
}
