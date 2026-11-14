namespace AIUsageMonitor.Core.Models;

/// <summary>
/// Everything that can be learned from the local Claude Code data beyond plain token counts, for a range of local days.
/// All of it is plain data, independent of how it is displayed.
/// </summary>
/// <param name="From">The first local day of the range.</param>
/// <param name="To">The last local day of the range.</param>
/// <param name="Totals">The usage of the whole range.</param>
/// <param name="Attribution">
/// The usage split along each dimension, keyed by the dimension name (<c>kind</c>, <c>skill</c>, <c>agent</c>,
/// <c>mcpServer</c>, <c>plugin</c>, <c>branch</c>, <c>effort</c>, <c>entrypoint</c>, <c>model</c>, <c>speed</c>);
/// each list is ordered by estimated cost, highest first. A response without a value for a dimension is left out of it.
/// </param>
/// <param name="Tools">The tools that were called, most-called first.</param>
/// <param name="ToolSummary">Totals about tool calls, results and permission decisions.</param>
/// <param name="Cache">The prompt-cache and thinking figures of the range.</param>
/// <param name="Compactions">The context compactions in the range, oldest first.</param>
/// <param name="RateLimits">The rate-limit rejections in the range, oldest first.</param>
/// <param name="CostChecks">Claude Code's own cost figure next to the estimate, for sessions it reported a cost for.</param>
/// <param name="Sessions">What is known about each session that had activity in the range.</param>
/// <param name="CostChecksExcluded">
/// The number of sessions in the range that have a Claude Code cost figure but no entry in <paramref name="CostChecks"/>,
/// because the session reaches outside the range or the transcripts that were read, or because its start time is unknown.
/// </param>
public sealed record InsightsReport(
    DateOnly From,
    DateOnly To,
    AttributionRow Totals,
    IReadOnlyDictionary<string, IReadOnlyList<AttributionRow>> Attribution,
    IReadOnlyList<ToolStat> Tools,
    ToolSummary ToolSummary,
    CacheInsights Cache,
    IReadOnlyList<CompactionEvent> Compactions,
    IReadOnlyList<RateLimitEvent> RateLimits,
    IReadOnlyList<CostCheck> CostChecks,
    IReadOnlyList<SessionInfo> Sessions,
    int CostChecksExcluded = 0);

/// <summary>
/// The usage of one value of an attribution dimension (e.g. one skill, one branch, sub-agents as a whole).
/// </summary>
/// <param name="Key">The value of the dimension.</param>
/// <param name="Responses">The number of model responses.</param>
/// <param name="Tokens">All tokens: input, output, cache reads and cache writes.</param>
/// <param name="InputTokens">The input tokens.</param>
/// <param name="OutputTokens">The output tokens, thinking included.</param>
/// <param name="CacheReadTokens">The tokens read from the prompt cache.</param>
/// <param name="CacheWriteTokens">The tokens written to the prompt cache.</param>
/// <param name="ThinkingTokens">The part of <paramref name="OutputTokens"/> spent on thinking.</param>
/// <param name="EstimatedCost">The estimated cost, in US dollars.</param>
public sealed record AttributionRow(
    string Key,
    long Responses,
    long Tokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingTokens,
    decimal EstimatedCost);

/// <summary>
/// How often a tool was called.
/// </summary>
/// <param name="Name">The tool name as Claude Code reports it (MCP tools look like <c>mcp__server__tool</c>).</param>
/// <param name="Calls">The number of calls.</param>
/// <param name="McpServer">The MCP server the tool belongs to, or <see langword="null"/> for a built-in tool.</param>
public sealed record ToolStat(string Name, long Calls, string? McpServer);

/// <summary>
/// Totals about tool use in a range.
/// </summary>
/// <param name="ToolCalls">The number of tool calls.</param>
/// <param name="ToolResults">The number of tool results.</param>
/// <param name="ToolErrors">The number of tool results that reported an error.</param>
/// <param name="Denials">The number of denied tool calls, by kind of denial.</param>
/// <param name="Decisions">The number of permission decisions, keyed <c>decision/source</c>.</param>
public sealed record ToolSummary(
    long ToolCalls,
    long ToolResults,
    long ToolErrors,
    IReadOnlyDictionary<string, long> Denials,
    IReadOnlyDictionary<string, long> Decisions);

/// <summary>
/// Prompt-cache and thinking figures of a range.
/// </summary>
/// <param name="Responses">The number of model responses.</param>
/// <param name="InputTokens">The uncached input tokens.</param>
/// <param name="CacheReadTokens">The tokens read from the cache.</param>
/// <param name="CacheWrite5mTokens">The tokens written with the 5-minute lifetime.</param>
/// <param name="CacheWrite1hTokens">The tokens written with the 1-hour lifetime.</param>
/// <param name="HitRate">The share of input-side tokens served from the cache (0 to 1).</param>
/// <param name="OutputTokens">The output tokens, thinking included.</param>
/// <param name="ThinkingTokens">The part of <paramref name="OutputTokens"/> spent on thinking.</param>
/// <param name="ThinkingShare">The share of output tokens spent on thinking (0 to 1).</param>
/// <param name="ThinkingResponses">The number of responses that thought.</param>
/// <param name="FastResponses">The number of responses served in fast mode.</param>
/// <param name="WebSearches">The number of web searches.</param>
/// <param name="WebFetches">The number of web fetches.</param>
/// <param name="MissReasons">Why the cache missed, most frequent first.</param>
public sealed record CacheInsights(
    long Responses,
    long InputTokens,
    long CacheReadTokens,
    long CacheWrite5mTokens,
    long CacheWrite1hTokens,
    double HitRate,
    long OutputTokens,
    long ThinkingTokens,
    double ThinkingShare,
    long ThinkingResponses,
    long FastResponses,
    long WebSearches,
    long WebFetches,
    IReadOnlyList<CacheMissStat> MissReasons);

/// <summary>
/// How often the prompt cache missed for one reason.
/// </summary>
/// <param name="Reason">The reason, as the API reports it.</param>
/// <param name="Count">The number of responses that missed for it.</param>
/// <param name="MissedInputTokens">The input tokens that could not be served from the cache.</param>
public sealed record CacheMissStat(string Reason, long Count, long MissedInputTokens);

/// <summary>
/// One compaction of a conversation's context.
/// </summary>
/// <param name="Time">When it happened.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Trigger">What triggered it (e.g. <c>auto</c>, <c>manual</c>).</param>
/// <param name="PreTokens">The context size before compaction, in tokens.</param>
/// <param name="PostTokens">The context size after compaction, in tokens.</param>
/// <param name="DurationMs">How long it took, in milliseconds.</param>
public sealed record CompactionEvent(
    DateTimeOffset Time, string? SessionId, string? Trigger, long PreTokens, long PostTokens, long DurationMs);

/// <summary>
/// One response that was rejected because a usage limit had been reached.
/// </summary>
/// <param name="Time">When it happened.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Status">The limit status Claude Code recorded.</param>
/// <param name="LimitType">Which limit was hit (e.g. five-hour or weekly).</param>
/// <param name="ResetsAt">When the limit resets, if recorded.</param>
/// <param name="OverageStatus">The overage status of the account.</param>
/// <param name="IsUsingOverage">Whether the account was using overage.</param>
public sealed record RateLimitEvent(
    DateTimeOffset Time,
    string? SessionId,
    string? Status,
    string? LimitType,
    DateTimeOffset? ResetsAt,
    string? OverageStatus,
    bool IsUsingOverage);

/// <summary>
/// Claude Code's own record of a session's cost next to this tool's estimate. Only meaningful when the range covers the
/// whole session, because Claude Code reports the session's total.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="ClaudeCostUsd">The cost Claude Code computed, in US dollars.</param>
/// <param name="EstimatedCostUsd">The estimate computed here from the transcript, in US dollars.</param>
/// <param name="LinesAdded">The lines of code added in the session.</param>
/// <param name="LinesRemoved">The lines of code removed in the session.</param>
/// <param name="ApiDurationMs">The time spent in API calls, in milliseconds.</param>
/// <param name="ToolDurationMs">The time spent running tools, in milliseconds.</param>
/// <param name="DurationMs">The wall-clock duration of the session, in milliseconds.</param>
public sealed record CostCheck(
    string SessionId,
    decimal ClaudeCostUsd,
    decimal EstimatedCostUsd,
    long LinesAdded,
    long LinesRemoved,
    double ApiDurationMs,
    double ToolDurationMs,
    double DurationMs);

/// <summary>
/// What is known about one session beyond its usage.
/// </summary>
/// <param name="SessionId">The session.</param>
/// <param name="Title">The user-chosen title, otherwise the generated one.</param>
/// <param name="AgentName">The name of the session's agent, if set.</param>
/// <param name="Branch">The git branch last seen in the session.</param>
/// <param name="Entrypoint">The entry point of the session (e.g. desktop app or CLI).</param>
/// <param name="Version">The Claude Code version that last wrote to the session.</param>
/// <param name="PullRequests">The pull requests linked to the session.</param>
/// <param name="SubagentResponses">The number of responses written by sub-agents.</param>
public sealed record SessionInfo(
    string SessionId,
    string? Title,
    string? AgentName,
    string? Branch,
    string? Entrypoint,
    string? Version,
    IReadOnlyList<string> PullRequests,
    long SubagentResponses);
