using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// A compact summary of a message's <c>content</c> blocks: how many blocks of each kind there are and the names of the
/// tools that were called. The text, tool inputs and tool results themselves are never retained.
/// </summary>
public sealed class ContentSummary
{
    /// <summary>
    /// Gets the shared summary of content without any recognised blocks (e.g. a plain string).
    /// </summary>
    public static ContentSummary Empty { get; } = new();

    /// <summary>
    /// Gets the number of <c>tool_use</c> blocks.
    /// </summary>
    public int ToolUseCount { get; init; }

    /// <summary>
    /// Gets the name of the tool of each <c>tool_use</c> block, in order (an empty string when the block has no name).
    /// </summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>
    /// Gets the number of <c>tool_result</c> blocks.
    /// </summary>
    public int ToolResultCount { get; init; }

    /// <summary>
    /// Gets the number of <c>tool_result</c> blocks that report an error.
    /// </summary>
    public int ToolResultErrorCount { get; init; }

    /// <summary>
    /// Gets the number of <c>thinking</c> (or redacted thinking) blocks.
    /// </summary>
    public int ThinkingBlockCount { get; init; }

    /// <summary>
    /// Gets the number of <c>text</c> blocks.
    /// </summary>
    public int TextBlockCount { get; init; }
}

/// <summary>
/// Represents the content of a message in a session transcript, including the message content, model used, role of the sender, and token usage information.
/// </summary>
public sealed class MessageContent
{
    /// <summary>
    /// Gets the summary of the message's content blocks. The content itself is not retained, because tool inputs and
    /// results can be very large and are not needed by any analytics.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(ContentSummaryConverter))]
    public ContentSummary Content
    {
        get => _content;
        init => _content = value ?? ContentSummary.Empty;
    }

    private readonly ContentSummary _content = ContentSummary.Empty;

    /// <summary>
    /// Gets the number of <c>tool_use</c> blocks in the message content.
    /// </summary>
    [JsonIgnore]
    public int ToolUseCount => Content.ToolUseCount;

    /// <summary>
    /// Gets the API-assigned identifier of the message (e.g. <c>msg_...</c>), used to detect
    /// duplicate transcript lines that carry the same usage.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Id { get; init; }

    /// <summary>
    /// Gets the name of the model that generated the message.
    /// </summary>
    [JsonPropertyName("model")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Model { get; init; }

    /// <summary>
    /// Gets the role of the message sender (e.g., "user" or "assistant").
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    /// <summary>
    /// Gets the reason the model stopped generating (e.g. <c>tool_use</c>, <c>end_turn</c>), when reported.
    /// </summary>
    [JsonPropertyName("stop_reason")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? StopReason { get; init; }

    /// <summary>
    /// Gets the token usage information associated with the message.
    /// </summary>
    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; init; }

    /// <summary>
    /// Gets the prompt-cache diagnostics of the message, when reported.
    /// </summary>
    [JsonPropertyName("diagnostics")]
    [JsonConverter(typeof(LenientObjectConverter<MessageDiagnostics>))]
    public MessageDiagnostics? Diagnostics { get; init; }
}

/// <summary>
/// The per-message diagnostics block.
/// </summary>
public sealed class MessageDiagnostics
{
    /// <summary>
    /// Gets why the prompt cache missed, when it did.
    /// </summary>
    [JsonPropertyName("cache_miss_reason")]
    public CacheMissReason? CacheMissReason { get; init; }
}

/// <summary>
/// Why the prompt cache missed for a response.
/// </summary>
public sealed class CacheMissReason
{
    /// <summary>
    /// Gets the kind of miss, as reported by the API.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Type { get; init; }

    /// <summary>
    /// Gets the number of input tokens that could not be served from the cache.
    /// </summary>
    [JsonPropertyName("cache_missed_input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long CacheMissedInputTokens { get; init; }
}

/// <summary>
/// The rate-limit state Claude Code records on an assistant line that was rejected for exceeding a limit.
/// </summary>
public sealed class QuotaLimits
{
    /// <summary>
    /// Gets the limit status (e.g. <c>rejected</c>).
    /// </summary>
    [JsonPropertyName("status")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Status { get; init; }

    /// <summary>
    /// Gets the Unix time, in seconds, at which the limit resets.
    /// </summary>
    [JsonPropertyName("resetsAt")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? ResetsAt { get; init; }

    /// <summary>
    /// Gets which limit was hit (e.g. the five-hour or the weekly one).
    /// </summary>
    [JsonPropertyName("rateLimitType")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? RateLimitType { get; init; }

    /// <summary>
    /// Gets the overage status of the account.
    /// </summary>
    [JsonPropertyName("overageStatus")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? OverageStatus { get; init; }

    /// <summary>
    /// Gets a value indicating whether the account was using overage when the line was written.
    /// </summary>
    [JsonPropertyName("isUsingOverage")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool IsUsingOverage { get; init; }
}

/// <summary>
/// How a tool permission request was decided.
/// </summary>
public sealed class PermissionDecision
{
    /// <summary>
    /// Gets the decision (e.g. <c>accept</c> or <c>reject</c>).
    /// </summary>
    [JsonPropertyName("decision")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Decision { get; init; }

    /// <summary>
    /// Gets what made the decision (e.g. a rule, the user or the auto-mode classifier).
    /// </summary>
    [JsonPropertyName("source")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Source { get; init; }
}

/// <summary>
/// Who or what produced a user line.
/// </summary>
public sealed class LineOrigin
{
    /// <summary>
    /// Gets the kind of origin (e.g. <c>human</c> or <c>task-notification</c>).
    /// </summary>
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Kind { get; init; }
}

/// <summary>
/// The metadata of a <c>compact_boundary</c> system line, written when the conversation context is compacted.
/// </summary>
public sealed class CompactMetadata
{
    /// <summary>
    /// Gets what triggered the compaction (e.g. <c>auto</c> or <c>manual</c>).
    /// </summary>
    [JsonPropertyName("trigger")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Trigger { get; init; }

    /// <summary>
    /// Gets the context size in tokens before compaction.
    /// </summary>
    [JsonPropertyName("preTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long PreTokens { get; init; }

    /// <summary>
    /// Gets the context size in tokens after compaction.
    /// </summary>
    [JsonPropertyName("postTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long PostTokens { get; init; }

    /// <summary>
    /// Gets how long the compaction took, in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long DurationMs { get; init; }
}

/// <summary>
/// One model's usage inside a Claude Code <c>cost-state</c> line.
/// </summary>
public sealed class CostStateModelUsage
{
    /// <summary>
    /// Gets the input tokens.
    /// </summary>
    [JsonPropertyName("inputTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long InputTokens { get; init; }

    /// <summary>
    /// Gets the output tokens (thinking tokens are included in this figure).
    /// </summary>
    [JsonPropertyName("outputTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long OutputTokens { get; init; }

    /// <summary>
    /// Gets the thinking tokens, a subset of <see cref="OutputTokens"/>.
    /// </summary>
    [JsonPropertyName("thinkingTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long ThinkingTokens { get; init; }

    /// <summary>
    /// Gets the cache-read tokens.
    /// </summary>
    [JsonPropertyName("cacheReadInputTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long CacheReadInputTokens { get; init; }

    /// <summary>
    /// Gets the cache-creation tokens.
    /// </summary>
    [JsonPropertyName("cacheCreationInputTokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long CacheCreationInputTokens { get; init; }

    /// <summary>
    /// Gets the number of web searches.
    /// </summary>
    [JsonPropertyName("webSearchRequests")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long WebSearchRequests { get; init; }

    /// <summary>
    /// Gets the cost in US dollars that Claude Code computed for this model.
    /// </summary>
    [JsonPropertyName("costUSD")]
    [JsonConverter(typeof(LenientRequiredDoubleConverter))]
    public double CostUsd { get; init; }
}

/// <summary>
/// Represents a single message in a session transcript, containing information about the message type, timestamp, UUID, session ID, current working directory, and the message content.
/// The same shape also carries the metadata lines of a transcript (titles, pull-request links, cost snapshots,
/// compaction boundaries), whose few fields are all optional.
/// </summary>
public sealed class SessionMessage
{
    /// <summary>
    /// Gets the identifier of the sub-agent that wrote the line, when it belongs to one.
    /// </summary>
    [JsonPropertyName("agentId")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AgentId { get; init; }

    /// <summary>
    /// Gets the name given to the agent of the session, on <c>agent-name</c> lines.
    /// </summary>
    [JsonPropertyName("agentName")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AgentName { get; init; }

    /// <summary>
    /// Gets the generated title of the session, on <c>ai-title</c> lines.
    /// </summary>
    [JsonPropertyName("aiTitle")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AiTitle { get; init; }

    /// <summary>
    /// Gets the model that Claude Code asked for in a second role (the advisor), when present.
    /// </summary>
    [JsonPropertyName("advisorModel")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AdvisorModel { get; init; }

    /// <summary>
    /// Gets the HTTP status of an API error, when the line records one.
    /// </summary>
    [JsonPropertyName("apiErrorStatus")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? ApiErrorStatus { get; init; }

    /// <summary>
    /// Gets the agent the cost of the line is attributed to (e.g. <c>Explore</c>), when set.
    /// </summary>
    [JsonPropertyName("attributionAgent")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AttributionAgent { get; init; }

    /// <summary>
    /// Gets the MCP server the cost of the line is attributed to, when set.
    /// </summary>
    [JsonPropertyName("attributionMcpServer")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AttributionMcpServer { get; init; }

    /// <summary>
    /// Gets the MCP tool the cost of the line is attributed to, when set.
    /// </summary>
    [JsonPropertyName("attributionMcpTool")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AttributionMcpTool { get; init; }

    /// <summary>
    /// Gets the plugin the cost of the line is attributed to, when set.
    /// </summary>
    [JsonPropertyName("attributionPlugin")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AttributionPlugin { get; init; }

    /// <summary>
    /// Gets the skill the cost of the line is attributed to, when set.
    /// </summary>
    [JsonPropertyName("attributionSkill")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? AttributionSkill { get; init; }

    /// <summary>
    /// Gets the compaction metadata, on <c>compact_boundary</c> system lines.
    /// </summary>
    [JsonPropertyName("compactMetadata")]
    [JsonConverter(typeof(LenientObjectConverter<CompactMetadata>))]
    public CompactMetadata? CompactMetadata { get; init; }

    /// <summary>
    /// Gets the current working directory recorded at the time the message was created.
    /// </summary>
    [JsonPropertyName("cwd")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Cwd { get; init; }

    /// <summary>
    /// Gets the user-chosen title of the session, on <c>custom-title</c> lines.
    /// </summary>
    [JsonPropertyName("customTitle")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? CustomTitle { get; init; }

    /// <summary>
    /// Gets the reasoning effort level recorded on the line (e.g. <c>low</c>, <c>medium</c>, <c>high</c>).
    /// </summary>
    [JsonPropertyName("effort")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Effort { get; init; }

    /// <summary>
    /// Gets the entry point of the session (e.g. <c>claude-desktop</c>, <c>cli</c>).
    /// </summary>
    [JsonPropertyName("entrypoint")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Entrypoint { get; init; }

    /// <summary>
    /// Gets the kind of API error, when the line records one.
    /// </summary>
    [JsonPropertyName("error")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Error { get; init; }

    /// <summary>
    /// Gets the git branch checked out when the line was written.
    /// </summary>
    [JsonPropertyName("gitBranch")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? GitBranch { get; init; }

    /// <summary>
    /// Gets a value indicating whether the line is a synthetic API-error message rather than a model response.
    /// </summary>
    [JsonPropertyName("isApiErrorMessage")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool IsApiErrorMessage { get; init; }

    /// <summary>
    /// Gets a value indicating whether the line is the summary Claude Code wrote when compacting the conversation.
    /// </summary>
    [JsonPropertyName("isCompactSummary")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool IsCompactSummary { get; init; }

    /// <summary>
    /// Gets a value indicating whether the line was written by a sub-agent instead of the main conversation.
    /// </summary>
    [JsonPropertyName("isSidechain")]
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool IsSidechain { get; init; }

    /// <summary>
    /// Gets the message content, including role, model, and token usage.
    /// </summary>
    [JsonPropertyName("message")]
    public MessageContent? Message { get; init; }

    /// <summary>
    /// Gets the per-model usage of a <c>cost-state</c> line.
    /// </summary>
    [JsonPropertyName("modelUsage")]
    [JsonConverter(typeof(LenientObjectConverter<Dictionary<string, CostStateModelUsage>>))]
    public Dictionary<string, CostStateModelUsage>? ModelUsage { get; init; }

    /// <summary>
    /// Gets who or what produced a user line.
    /// </summary>
    [JsonPropertyName("origin")]
    [JsonConverter(typeof(LenientOriginConverter))]
    public LineOrigin? Origin { get; init; }

    /// <summary>
    /// Gets how a tool permission request was decided, on the user line that carries the tool result.
    /// </summary>
    [JsonPropertyName("permissionDecision")]
    [JsonConverter(typeof(LenientObjectConverter<PermissionDecision>))]
    public PermissionDecision? PermissionDecision { get; init; }

    /// <summary>
    /// Gets the permission mode in effect (e.g. <c>auto</c>, <c>plan</c>, <c>acceptEdits</c>).
    /// </summary>
    [JsonPropertyName("permissionMode")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets the reasoning effort of this turn when it differs from the session default.
    /// </summary>
    [JsonPropertyName("perTurnEffort")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? PerTurnEffort { get; init; }

    /// <summary>
    /// Gets the number of the pull request, on <c>pr-link</c> lines.
    /// </summary>
    [JsonPropertyName("prNumber")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? PrNumber { get; init; }

    /// <summary>
    /// Gets the repository of the pull request (<c>owner/name</c>), on <c>pr-link</c> lines.
    /// </summary>
    [JsonPropertyName("prRepository")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? PrRepository { get; init; }

    /// <summary>
    /// Gets the URL of the pull request, on <c>pr-link</c> lines.
    /// </summary>
    [JsonPropertyName("prUrl")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? PrUrl { get; init; }

    /// <summary>
    /// Gets the rate-limit state of an assistant line that was rejected for exceeding a limit.
    /// </summary>
    [JsonPropertyName("quotaLimits")]
    [JsonConverter(typeof(LenientObjectConverter<QuotaLimits>))]
    public QuotaLimits? QuotaLimits { get; init; }

    /// <summary>
    /// Gets the API request identifier associated with this transcript line, used together with
    /// <see cref="MessageContent.Id"/> to detect duplicate lines for the same assistant response.
    /// </summary>
    [JsonPropertyName("requestId")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? RequestId { get; init; }

    /// <summary>
    /// Gets the model that was requested, which can differ from the model that answered.
    /// </summary>
    [JsonPropertyName("requestedModel")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? RequestedModel { get; init; }

    /// <summary>
    /// Gets the identifier of the session this message belongs to.
    /// </summary>
    [JsonPropertyName("sessionId")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the Unix time, in milliseconds, at which the session of a <c>cost-state</c> line started, which bounds the
    /// transcript lines its totals can cover.
    /// </summary>
    [JsonPropertyName("startTime")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? StartTimeMs { get; init; }

    /// <summary>
    /// Gets the subtype of a <c>system</c> line (e.g. <c>compact_boundary</c>, <c>stop_hook_summary</c>).
    /// </summary>
    [JsonPropertyName("subtype")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Subtype { get; init; }

    /// <summary>
    /// Gets how long the model thought before answering, in milliseconds, when reported.
    /// </summary>
    [JsonPropertyName("thinkingDurationMs")]
    [JsonConverter(typeof(LenientLongConverter))]
    public long? ThinkingDurationMs { get; init; }

    /// <summary>
    /// Gets the timestamp indicating when the message was recorded, parsed once at read time;
    /// <see langword="null"/> when the line has no timestamp or it cannot be parsed.
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(LenientTimestampConverter))]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Gets why a tool call was denied (e.g. <c>permission-rule</c>, <c>user-rejected</c>), when it was.
    /// </summary>
    [JsonPropertyName("toolDenialKind")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ToolDenialKind { get; init; }

    /// <summary>
    /// Gets the total time spent in API calls so far, in milliseconds, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalAPIDuration")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? TotalApiDurationMs { get; init; }

    /// <summary>
    /// Gets the cost in US dollars that Claude Code computed for the session so far, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalCostUSD")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? TotalCostUsd { get; init; }

    /// <summary>
    /// Gets the wall-clock duration of the session so far, in milliseconds, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalDuration")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? TotalDurationMs { get; init; }

    /// <summary>
    /// Gets the number of lines of code added in the session so far, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalLinesAdded")]
    public long? TotalLinesAdded { get; init; }

    /// <summary>
    /// Gets the number of lines of code removed in the session so far, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalLinesRemoved")]
    public long? TotalLinesRemoved { get; init; }

    /// <summary>
    /// Gets the time spent running tools so far, in milliseconds, on <c>cost-state</c> lines.
    /// </summary>
    [JsonPropertyName("totalToolDuration")]
    [JsonConverter(typeof(LenientDoubleConverter))]
    public double? TotalToolDurationMs { get; init; }

    /// <summary>
    /// Gets the type of the message (e.g., "user" or "assistant").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>
    /// Gets the unique identifier of the message.
    /// </summary>
    [JsonPropertyName("uuid")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Uuid { get; init; }

    /// <summary>
    /// Gets the Claude Code version that wrote the line.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Version { get; init; }
}

/// <summary>
/// Represents token usage statistics for a message, including cache and input/output token counts.
/// </summary>
public sealed class TokenUsage
{
    /// <summary>
    /// Gets the number of tokens used to create the cache.
    /// </summary>
    [JsonPropertyName("cache_creation_input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long CacheCreationInputTokens { get; init; }

    /// <summary>
    /// Gets the breakdown of cache-creation tokens by TTL (5-minute vs 1-hour), when the
    /// transcript line reports it. <see langword="null"/> for older transcript lines that
    /// only carry the combined <see cref="CacheCreationInputTokens"/> total.
    /// </summary>
    [JsonPropertyName("cache_creation")]
    public CacheCreationDetail? CacheCreation { get; init; }

    /// <summary>
    /// Gets the number of tokens read from the cache.
    /// </summary>
    [JsonPropertyName("cache_read_input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long CacheReadInputTokens { get; init; }

    /// <summary>
    /// Gets the number of input tokens consumed by the message.
    /// </summary>
    [JsonPropertyName("input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long InputTokens { get; init; }

    /// <summary>
    /// Gets the details of the output tokens, when reported.
    /// </summary>
    [JsonPropertyName("output_tokens_details")]
    [JsonConverter(typeof(LenientObjectConverter<OutputTokensDetails>))]
    public OutputTokensDetails? OutputTokensDetails { get; init; }

    /// <summary>
    /// Gets the number of output tokens produced by the message. Thinking tokens are part of this figure.
    /// </summary>
    [JsonPropertyName("output_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long OutputTokens { get; init; }

    /// <summary>
    /// Gets the server-side tool usage of the message (web search and web fetch), when reported.
    /// </summary>
    [JsonPropertyName("server_tool_use")]
    [JsonConverter(typeof(LenientObjectConverter<ServerToolUse>))]
    public ServerToolUse? ServerToolUse { get; init; }

    /// <summary>
    /// Gets the service tier the request ran on (e.g. <c>standard</c>), when reported.
    /// </summary>
    [JsonPropertyName("service_tier")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ServiceTier { get; init; }

    /// <summary>
    /// Gets the speed mode the request ran in (e.g. <c>standard</c> or <c>fast</c>), when reported.
    /// </summary>
    [JsonPropertyName("speed")]
    [JsonConverter(typeof(LenientStringConverter))]
    public string? Speed { get; init; }

    /// <summary>
    /// Gets the thinking tokens of the response, a subset of <see cref="OutputTokens"/>; zero when not reported.
    /// </summary>
    [JsonIgnore]
    public long ThinkingTokens => OutputTokensDetails?.ThinkingTokens ?? 0;

    /// <summary>
    /// Gets the tokens of the response in all four kinds: input, output, cache reads and cache writes.
    /// </summary>
    [JsonIgnore]
    public long TotalTokens => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

/// <summary>
/// The details of a response's output tokens.
/// </summary>
public sealed class OutputTokensDetails
{
    /// <summary>
    /// Gets the number of output tokens spent on thinking.
    /// </summary>
    [JsonPropertyName("thinking_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long ThinkingTokens { get; init; }
}

/// <summary>
/// The server-side tool usage of a response.
/// </summary>
public sealed class ServerToolUse
{
    /// <summary>
    /// Gets the number of web fetches.
    /// </summary>
    [JsonPropertyName("web_fetch_requests")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long WebFetchRequests { get; init; }

    /// <summary>
    /// Gets the number of web searches.
    /// </summary>
    [JsonPropertyName("web_search_requests")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long WebSearchRequests { get; init; }
}

/// <summary>
/// Represents the breakdown of cache-creation tokens by TTL, as reported by the Anthropic API.
/// </summary>
public sealed class CacheCreationDetail
{
    /// <summary>
    /// Gets the number of cache-creation tokens written with a 5-minute TTL.
    /// </summary>
    [JsonPropertyName("ephemeral_5m_input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long Ephemeral5mInputTokens { get; init; }

    /// <summary>
    /// Gets the number of cache-creation tokens written with a 1-hour TTL.
    /// </summary>
    [JsonPropertyName("ephemeral_1h_input_tokens")]
    [JsonConverter(typeof(LenientInt64Converter))]
    public long Ephemeral1hInputTokens { get; init; }
}
