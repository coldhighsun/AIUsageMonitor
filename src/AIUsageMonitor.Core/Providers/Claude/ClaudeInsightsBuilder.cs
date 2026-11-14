using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Reads everything the session transcripts say beyond plain token counts, in one pass over the cached rows: cost split
/// by sub-agent, skill, agent, MCP server, plugin, branch, effort and more; tool use; prompt-cache and thinking figures;
/// compactions; rate-limit rejections; Claude Code's own cost figures; and session titles and pull requests.
/// Lines are de-duplicated exactly as in the other transcript aggregations. Conversation text, tool inputs and tool
/// outputs are never read into the result.
/// </summary>
/// <param name="locator">Locates the session transcripts.</param>
/// <param name="sessionFileCache">The session file cache that supplies the parsed rows.</param>
/// <param name="costCalculator">The calculator that prices token usage.</param>
/// <param name="timeProvider">The clock and local time zone used to place lines on local days; defaults to the system clock.</param>
public sealed class ClaudeInsightsBuilder(
    ClaudeDataLocator locator,
    SessionFileCache sessionFileCache,
    CostCalculator costCalculator,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The name of the dimension that separates the main conversation from sub-agents.
    /// </summary>
    public const string KindDimension = "kind";

    /// <summary>
    /// The prefix of the tool name of an MCP tool.
    /// </summary>
    private const string McpPrefix = "mcp__";

    /// <summary>
    /// The clock whose local time zone decides which calendar day a line belongs to.
    /// </summary>
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Builds the insights for a range of local days.
    /// </summary>
    /// <param name="from">The first local day to include.</param>
    /// <param name="to">The last local day to include.</param>
    /// <param name="progress">An optional progress reporter (0-100).</param>
    /// <returns>The insights of the range.</returns>
    public InsightsReport Build(DateOnly from, DateOnly to, IProgress<int>? progress = null)
    {
        var zone = _clock.LocalTimeZone;
        var coarseFrom = SessionUsageBuilder.CoarseBound(from, -1);
        var coarseTo = SessionUsageBuilder.CoarseBound(to, 2);

        var files = sessionFileCache.GetFilesModifiedSince(locator.GetSessionFiles(), coarseFrom)
            .Order(StringComparer.Ordinal)
            .ToList();
        sessionFileCache.WarmUp(files, progress);
        if (files.Count == 0)
        {
            progress?.Report(100);
        }

        var state = new PassState();
        var deduplicator = new TranscriptDeduplicator();

        foreach (var file in files)
        {
            IReadOnlyList<SessionMessage> rows;
            try
            {
                rows = sessionFileCache.GetRows(file);
            }
            catch
            {
                continue;
            }

            // Lines without a session id take the session of their transcript, as in SessionUsageBuilder.
            var fallbackSessionId = rows.FirstOrDefault(r => r.SessionId is not null)?.SessionId
                                    ?? Path.GetFileNameWithoutExtension(file);

            foreach (var row in rows)
            {
                var sessionId = row.SessionId ?? fallbackSessionId;
                if (row.Timestamp is not { } ts)
                {
                    state.NoteMetadata(row, sessionId);

                    continue;
                }

                if (ts < coarseFrom || ts >= coarseTo)
                {
                    // Not counted, but the session reaches outside the range, so its whole-session figures are incomplete.
                    state.MarkPartial(sessionId);

                    continue;
                }

                if (!deduplicator.TryAddLine(row))
                {
                    continue;
                }

                state.NoteMetadata(row, sessionId);
                Visit(row, sessionId, ts, zone, from, to, deduplicator, state);
            }
        }

        return state.ToReport(from, to, coarseFrom, costCalculator);
    }

    /// <summary>
    /// Folds one timestamped, not yet counted line into the pass state.
    /// </summary>
    /// <param name="row">The line.</param>
    /// <param name="sessionId">The session the line belongs to.</param>
    /// <param name="ts">The time of the line.</param>
    /// <param name="zone">The local time zone.</param>
    /// <param name="from">The first local day of the range.</param>
    /// <param name="to">The last local day of the range.</param>
    /// <param name="deduplicator">Tells whether a response's usage was already counted.</param>
    /// <param name="state">The state being built.</param>
    private static void Visit(
        SessionMessage row, string sessionId, DateTimeOffset ts, TimeZoneInfo zone, DateOnly from, DateOnly to,
        TranscriptDeduplicator deduplicator, PassState state)
    {
        var usage = row.Message?.Usage;
        var model = row.Message?.Model ?? "unknown";

        // A streamed response is several lines sharing one usage; the first one claims it, wherever it falls.
        var claimsUsage = row.Type == "assistant" && usage is not null && model != "<synthetic>" && deduplicator.TryAddUsage(row);

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ts, zone).DateTime);
        if (day < from || day > to)
        {
            state.MarkPartial(sessionId);

            return;
        }

        state.Session(sessionId).Note(row);

        switch (row.Type)
        {
            case "assistant":
                state.VisitAssistant(row, sessionId, ts, claimsUsage, model, usage);
                break;
            case "user":
                state.VisitUser(row);
                break;
            case "system" when row.CompactMetadata is { } compact:
                state.Compactions.Add(new(ts, sessionId, compact.Trigger, compact.PreTokens, compact.PostTokens, compact.DurationMs));
                break;
        }
    }

    /// <summary>
    /// Splits an MCP tool name into its server.
    /// </summary>
    /// <param name="toolName">The tool name.</param>
    /// <returns>The server name, or <see langword="null"/> when the tool is not an MCP tool.</returns>
    internal static string? McpServerOf(string toolName)
    {
        if (!toolName.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = toolName.AsSpan(McpPrefix.Length);
        var end = rest.IndexOf("__", StringComparison.Ordinal);

        return (end < 0 ? rest : rest[..end]).ToString();
    }

    /// <summary>
    /// The usage collected for one value of an attribution dimension.
    /// </summary>
    private sealed class UsageAccumulator
    {
        /// <summary>
        /// The tokens per model, kept apart so each model is priced at its own rates.
        /// </summary>
        public Dictionary<string, ModelTokenTotals> Models { get; } = [];

        /// <summary>
        /// Gets or sets the number of responses.
        /// </summary>
        public long Responses { get; set; }

        /// <summary>
        /// Gets or sets the thinking tokens.
        /// </summary>
        public long Thinking { get; set; }

        /// <summary>
        /// Adds one response.
        /// </summary>
        /// <param name="model">The model that answered.</param>
        /// <param name="usage">The usage of the response.</param>
        public void Add(string model, TokenUsage usage)
        {
            Responses++;
            Thinking += usage.ThinkingTokens;
            ModelTokenTotals.Record(Models, model, usage);
        }

        /// <summary>
        /// Turns the accumulated usage into a row.
        /// </summary>
        /// <param name="key">The value of the dimension.</param>
        /// <param name="calculator">The calculator that prices the tokens.</param>
        /// <returns>The row.</returns>
        public AttributionRow ToRow(string key, CostCalculator calculator)
        {
            long input = 0, output = 0, read = 0, write = 0;
            foreach (var totals in Models.Values)
            {
                input += totals.Input;
                output += totals.Output;
                read += totals.CacheRead;
                write += totals.CacheWrite;
            }

            return new(key, Responses, input + output + read + write, input, output, read, write, Thinking,
                ModelTokenTotals.EstimateCost(Models, calculator));
        }
    }

    /// <summary>
    /// What is collected about one session.
    /// </summary>
    private sealed class SessionState
    {
        /// <summary>
        /// Gets or sets the user-chosen title.
        /// </summary>
        public string? CustomTitle { get; set; }

        /// <summary>
        /// Gets or sets the generated title.
        /// </summary>
        public string? AiTitle { get; set; }

        /// <summary>
        /// Gets or sets the name of the session's agent.
        /// </summary>
        public string? AgentName { get; set; }

        /// <summary>
        /// Gets or sets the git branch last seen.
        /// </summary>
        public string? Branch { get; set; }

        /// <summary>
        /// Gets or sets the entry point.
        /// </summary>
        public string? Entrypoint { get; set; }

        /// <summary>
        /// Gets or sets the Claude Code version last seen.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets the linked pull requests.
        /// </summary>
        public SortedSet<string> PullRequests { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets the number of responses written by sub-agents.
        /// </summary>
        public long SubagentResponses { get; set; }

        /// <summary>
        /// Gets or sets the last cost snapshot Claude Code wrote.
        /// </summary>
        public SessionMessage? CostState { get; set; }

        /// <summary>
        /// Gets the tokens of the session's responses inside the range, per model.
        /// </summary>
        public Dictionary<string, ModelTokenTotals> Usage { get; } = [];

        /// <summary>
        /// Gets or sets a value indicating whether the session has lines in the range.
        /// </summary>
        public bool InRange { get; set; }

        /// <summary>
        /// Notes the descriptive fields of a line inside the range.
        /// </summary>
        /// <param name="row">The line.</param>
        public void Note(SessionMessage row)
        {
            InRange = true;
            Branch = row.GitBranch ?? Branch;
            Entrypoint = row.Entrypoint ?? Entrypoint;
            Version = row.Version ?? Version;
        }
    }

    /// <summary>
    /// The state of one pass over the transcripts.
    /// </summary>
    private sealed class PassState
    {
        /// <summary>
        /// The dimensions the responses are split by, in the order they are reported.
        /// </summary>
        private static readonly string[] Dimensions =
            [KindDimension, "skill", "agent", "mcpServer", "plugin", "branch", "effort", "entrypoint", "model", "speed"];

        /// <summary>
        /// The usage per dimension and value.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, UsageAccumulator>> _attribution =
            Dimensions.ToDictionary(d => d, _ => new Dictionary<string, UsageAccumulator>(StringComparer.Ordinal));

        /// <summary>
        /// The usage of the whole range.
        /// </summary>
        private readonly UsageAccumulator _totals = new();

        /// <summary>
        /// The sessions, by id.
        /// </summary>
        private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

        /// <summary>
        /// The sessions that have lines outside the range, whose estimate is therefore incomplete.
        /// </summary>
        private readonly HashSet<string> _partialSessions = new(StringComparer.Ordinal);

        /// <summary>
        /// The number of calls per tool.
        /// </summary>
        private readonly Dictionary<string, long> _tools = new(StringComparer.Ordinal);

        /// <summary>
        /// The number of denied calls by kind.
        /// </summary>
        private readonly SortedDictionary<string, long> _denials = new(StringComparer.Ordinal);

        /// <summary>
        /// The number of permission decisions by decision and source.
        /// </summary>
        private readonly SortedDictionary<string, long> _decisions = new(StringComparer.Ordinal);

        /// <summary>
        /// The cache misses by reason.
        /// </summary>
        private readonly Dictionary<string, (long Count, long Tokens)> _misses = new(StringComparer.Ordinal);

        /// <summary>
        /// The cache and thinking totals.
        /// </summary>
        private (long Responses, long Input, long Read, long Write5m, long Write1h, long Output, long Thinking,
            long ThinkingResponses, long Fast, long Searches, long Fetches) _cache;

        /// <summary>
        /// The number of tool results and of those that were errors.
        /// </summary>
        private (long Results, long Errors) _toolResults;

        /// <summary>
        /// Gets the compactions found.
        /// </summary>
        public List<CompactionEvent> Compactions { get; } = [];

        /// <summary>
        /// Gets the rate-limit rejections found.
        /// </summary>
        public List<RateLimitEvent> RateLimits { get; } = [];

        /// <summary>
        /// Gets the state of a session, creating it on first use.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <returns>The session's state.</returns>
        public SessionState Session(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                session = new SessionState();
                _sessions[sessionId] = session;
            }

            return session;
        }

        /// <summary>
        /// Records the metadata a line carries (titles, pull request, cost snapshot), whatever its day.
        /// </summary>
        /// <param name="row">The line.</param>
        /// <param name="sessionId">The session the line belongs to.</param>
        public void NoteMetadata(SessionMessage row, string sessionId)
        {
            // Each field is checked on its own: a line may carry more than one of them.
            if (row.CustomTitle is not null)
            {
                Session(sessionId).CustomTitle = row.CustomTitle;
            }

            if (row.AiTitle is not null)
            {
                Session(sessionId).AiTitle = row.AiTitle;
            }

            if (row.AgentName is not null)
            {
                Session(sessionId).AgentName = row.AgentName;
            }

            if (row.PrUrl is not null)
            {
                Session(sessionId).PullRequests.Add(row.PrUrl);
            }

            if (row.TotalCostUsd is not null)
            {
                // Snapshots carry the session's running totals and have no timestamp, and a resumed session can have one
                // in each transcript, so the latest is the one with the longest duration (then the highest cost), not
                // whichever file happens to be read last.
                var session = Session(sessionId);
                if (session.CostState is not { } current || IsNewerSnapshot(row, current))
                {
                    session.CostState = row;
                }
            }
        }

        /// <summary>
        /// Notes that a session has lines outside the range.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        public void MarkPartial(string sessionId)
        {
            _partialSessions.Add(sessionId);
        }

        /// <summary>
        /// Folds an assistant line into the totals.
        /// </summary>
        /// <param name="row">The line.</param>
        /// <param name="sessionId">The session the line belongs to.</param>
        /// <param name="ts">The time of the line.</param>
        /// <param name="claimsUsage">Whether this line carries the usage of its response for the first time.</param>
        /// <param name="model">The model that answered.</param>
        /// <param name="usage">The usage of the response, if any.</param>
        public void VisitAssistant(SessionMessage row, string sessionId, DateTimeOffset ts, bool claimsUsage, string model, TokenUsage? usage)
        {
            if (row.Message?.Content is { ToolNames.Count: > 0 } content)
            {
                foreach (var name in content.ToolNames)
                {
                    _tools[name] = _tools.GetValueOrDefault(name) + 1;
                }
            }

            if (row.QuotaLimits is { } quota)
            {
                RateLimits.Add(new(
                    ts,
                    sessionId,
                    quota.Status,
                    quota.RateLimitType,
                    quota.ResetsAt is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null,
                    quota.OverageStatus,
                    quota.IsUsingOverage));
            }

            if (!claimsUsage || usage is null)
            {
                return;
            }

            var session = Session(sessionId);
            ModelTokenTotals.Record(session.Usage, model, usage);
            if (row.IsSidechain)
            {
                session.SubagentResponses++;
            }

            _totals.Add(model, usage);
            Attribute(KindDimension, row.IsSidechain ? "subagent" : "main", model, usage);
            Attribute("skill", row.AttributionSkill, model, usage);
            Attribute("agent", row.AttributionAgent, model, usage);
            Attribute("mcpServer", row.AttributionMcpServer, model, usage);
            Attribute("plugin", row.AttributionPlugin, model, usage);
            Attribute("branch", row.GitBranch, model, usage);
            Attribute("effort", row.Effort, model, usage);
            Attribute("entrypoint", row.Entrypoint, model, usage);
            Attribute("model", model, model, usage);
            Attribute("speed", usage.Speed, model, usage);

            var (write5m, write1h) = usage.CacheCreation is { } detail
                ? (detail.Ephemeral5mInputTokens, detail.Ephemeral1hInputTokens)
                : (usage.CacheCreationInputTokens, 0L);
            _cache.Responses++;
            _cache.Input += usage.InputTokens;
            _cache.Read += usage.CacheReadInputTokens;
            _cache.Write5m += write5m;
            _cache.Write1h += write1h;
            _cache.Output += usage.OutputTokens;
            _cache.Thinking += usage.ThinkingTokens;
            _cache.ThinkingResponses += usage.ThinkingTokens > 0 ? 1 : 0;
            _cache.Fast += string.Equals(usage.Speed, "fast", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _cache.Searches += usage.ServerToolUse?.WebSearchRequests ?? 0;
            _cache.Fetches += usage.ServerToolUse?.WebFetchRequests ?? 0;

            if (row.Message?.Diagnostics?.CacheMissReason is { Type: { } reason } miss)
            {
                var (count, tokens) = _misses.GetValueOrDefault(reason);
                _misses[reason] = (count + 1, tokens + miss.CacheMissedInputTokens);
            }
        }

        /// <summary>
        /// Folds a user line (tool results, permission decisions) into the totals.
        /// </summary>
        /// <param name="row">The line.</param>
        public void VisitUser(SessionMessage row)
        {
            if (row.Message?.Content is { } content)
            {
                _toolResults = (_toolResults.Results + content.ToolResultCount, _toolResults.Errors + content.ToolResultErrorCount);
            }

            if (row.ToolDenialKind is { } denial)
            {
                _denials[denial] = _denials.GetValueOrDefault(denial) + 1;
            }

            if (row.PermissionDecision is { Decision: { } decision } permission)
            {
                var key = $"{decision}/{permission.Source ?? "unknown"}";
                _decisions[key] = _decisions.GetValueOrDefault(key) + 1;
            }
        }

        /// <summary>
        /// Builds the report.
        /// </summary>
        /// <param name="from">The first local day of the range.</param>
        /// <param name="to">The last local day of the range.</param>
        /// <param name="coarseFrom">The earliest moment of the transcripts that were read.</param>
        /// <param name="calculator">The calculator that prices the tokens.</param>
        /// <returns>The report.</returns>
        public InsightsReport ToReport(DateOnly from, DateOnly to, DateTimeOffset coarseFrom, CostCalculator calculator)
        {
            var attribution = new Dictionary<string, IReadOnlyList<AttributionRow>>(StringComparer.Ordinal);
            foreach (var dimension in Dimensions)
            {
                attribution[dimension] = _attribution[dimension]
                    .Select(pair => pair.Value.ToRow(pair.Key, calculator))
                    .OrderByDescending(r => r.EstimatedCost)
                    .ThenByDescending(r => r.Tokens)
                    .ThenBy(r => r.Key, StringComparer.Ordinal)
                    .ToList();
            }

            var tools = _tools
                .Select(pair => new ToolStat(pair.Key, pair.Value, McpServerOf(pair.Key)))
                .OrderByDescending(t => t.Calls)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();

            var inputSide = _cache.Input + _cache.Read + _cache.Write5m + _cache.Write1h;
            var cache = new CacheInsights(
                _cache.Responses, _cache.Input, _cache.Read, _cache.Write5m, _cache.Write1h,
                inputSide == 0 ? 0 : (double)_cache.Read / inputSide,
                _cache.Output, _cache.Thinking,
                _cache.Output == 0 ? 0 : (double)_cache.Thinking / _cache.Output,
                _cache.ThinkingResponses, _cache.Fast, _cache.Searches, _cache.Fetches,
                _misses.Select(pair => new CacheMissStat(pair.Key, pair.Value.Count, pair.Value.Tokens))
                    .OrderByDescending(m => m.Count)
                    .ThenBy(m => m.Reason, StringComparer.Ordinal)
                    .ToList());

            var inRange = _sessions.Where(pair => pair.Value.InRange).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList();

            var costChecks = inRange
                .Where(pair => pair.Value.CostState is { } snapshot
                               && !_partialSessions.Contains(pair.Key)
                               && StartedInsideReadTranscripts(snapshot, coarseFrom))
                .Select(pair =>
                {
                    var state = pair.Value.CostState!;
                    // A session kept here has no line outside the range, so its in-range usage is its whole usage.
                    var estimate = ModelTokenTotals.EstimateCost(pair.Value.Usage, calculator);

                    return new CostCheck(
                        pair.Key,
                        (decimal)(state.TotalCostUsd ?? 0),
                        estimate,
                        state.TotalLinesAdded ?? 0,
                        state.TotalLinesRemoved ?? 0,
                        state.TotalApiDurationMs ?? 0,
                        state.TotalToolDurationMs ?? 0,
                        state.TotalDurationMs ?? 0);
                })
                .ToList();

            var sessions = inRange
                .Select(pair => new SessionInfo(
                    pair.Key,
                    pair.Value.CustomTitle ?? pair.Value.AiTitle,
                    pair.Value.AgentName,
                    pair.Value.Branch,
                    pair.Value.Entrypoint,
                    pair.Value.Version,
                    [.. pair.Value.PullRequests],
                    pair.Value.SubagentResponses))
                .ToList();

            return new InsightsReport(
                from,
                to,
                _totals.ToRow("total", calculator),
                attribution,
                tools,
                new ToolSummary(
                    _tools.Values.Sum(),
                    _toolResults.Results,
                    _toolResults.Errors,
                    new Dictionary<string, long>(_denials),
                    new Dictionary<string, long>(_decisions)),
                cache,
                Compactions.OrderBy(c => c.Time).ToList(),
                RateLimits.OrderBy(r => r.Time).ToList(),
                costChecks,
                sessions,
                inRange.Count(pair => pair.Value.CostState is not null) - costChecks.Count);
        }

        /// <summary>
        /// Tells whether a cost snapshot is newer than the one held: it covers a longer duration, or the same duration at a higher cost.
        /// </summary>
        /// <param name="candidate">The snapshot found.</param>
        /// <param name="current">The snapshot held.</param>
        /// <returns><see langword="true"/> when <paramref name="candidate"/> should replace <paramref name="current"/>.</returns>
        private static bool IsNewerSnapshot(SessionMessage candidate, SessionMessage current)
        {
            var byDuration = (candidate.TotalDurationMs ?? 0).CompareTo(current.TotalDurationMs ?? 0);

            return byDuration != 0 ? byDuration > 0 : (candidate.TotalCostUsd ?? 0) >= (current.TotalCostUsd ?? 0);
        }

        /// <summary>
        /// Tells whether every transcript line a snapshot's totals can cover was read. A transcript not modified since the
        /// range began is not read, so a session that started before that moment may have lines that were never seen.
        /// </summary>
        /// <param name="snapshot">The cost snapshot.</param>
        /// <param name="coarseFrom">The earliest moment of the transcripts that were read.</param>
        /// <returns><see langword="false"/> when the session started earlier, or when its start is unknown.</returns>
        private static bool StartedInsideReadTranscripts(SessionMessage snapshot, DateTimeOffset coarseFrom)
        {
            return snapshot.StartTimeMs is { } start && DateTimeOffset.FromUnixTimeMilliseconds(start) >= coarseFrom;
        }

        /// <summary>
        /// Adds a response to one value of a dimension, unless the response has no value for it.
        /// </summary>
        /// <param name="dimension">The dimension.</param>
        /// <param name="key">The value, or <see langword="null"/> when the response has none.</param>
        /// <param name="model">The model that answered.</param>
        /// <param name="usage">The usage of the response.</param>
        private void Attribute(string dimension, string? key, string model, TokenUsage usage)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var values = _attribution[dimension];
            if (!values.TryGetValue(key, out var accumulator))
            {
                accumulator = new UsageAccumulator();
                values[key] = accumulator;
            }

            accumulator.Add(model, usage);
        }
    }
}
