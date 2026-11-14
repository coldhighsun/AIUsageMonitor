using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests that the extra transcript fields (sub-agent flag, attribution, tool names, thinking, cache diagnostics, titles,
/// pull requests, cost snapshots, compaction and rate limits) are parsed and survive the on-disk row cache.
/// </summary>
public sealed class SessionRowExtrasTests : IDisposable
{
    /// <summary>
    /// An assistant line that carries most of the extra fields.
    /// </summary>
    private const string AssistantLine =
        """{"type":"assistant","timestamp":"2026-08-01T10:30:00Z","sessionId":"s1","uuid":"u1","requestId":"r1","isSidechain":true,"agentId":"a1","gitBranch":"main","entrypoint":"cli","permissionMode":"plan","effort":"high","perTurnEffort":"low","requestedModel":"opus","advisorModel":"sonnet","attributionSkill":"brainstorming","attributionAgent":"Explore","attributionMcpServer":"srv","attributionMcpTool":"tool","attributionPlugin":"plug","thinkingDurationMs":1234,"version":"2.1.0","message":{"id":"m1","role":"assistant","model":"opus","stop_reason":"tool_use","content":[{"type":"thinking","thinking":"x"},{"type":"text","text":"hi"},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}},{"type":"tool_use","id":"t2","name":"mcp__a__b","input":{}}],"usage":{"input_tokens":1,"output_tokens":50,"cache_read_input_tokens":2,"cache_creation_input_tokens":3,"service_tier":"standard","speed":"fast","output_tokens_details":{"thinking_tokens":20},"server_tool_use":{"web_search_requests":2,"web_fetch_requests":1}},"diagnostics":{"cache_miss_reason":{"type":"system_changed","cache_missed_input_tokens":999}}}}""";

    /// <summary>
    /// A user line carrying tool results, a permission decision and an origin.
    /// </summary>
    private const string UserLine =
        """{"type":"user","timestamp":"2026-08-01T10:31:00Z","sessionId":"s1","uuid":"u2","origin":{"kind":"human"},"toolDenialKind":"user-rejected","permissionDecision":{"decision":"reject","source":"user"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","is_error":true,"content":"boom"},{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}""";

    /// <summary>
    /// The metadata lines of a transcript.
    /// </summary>
    private static readonly string[] MetadataLines =
    [
        """{"type":"ai-title","sessionId":"s1","aiTitle":"Fix the bug"}""",
        """{"type":"custom-title","sessionId":"s1","customTitle":"My title"}""",
        """{"type":"agent-name","sessionId":"s1","agentName":"worker"}""",
        """{"type":"pr-link","sessionId":"s1","prNumber":42,"prUrl":"https://github.com/o/r/pull/42","prRepository":"o/r"}""",
        """{"type":"cost-state","sessionId":"s1","totalCostUSD":1.5,"totalAPIDuration":100,"totalToolDuration":50,"totalLinesAdded":10,"totalLinesRemoved":2,"totalDuration":9000,"modelUsage":{"opus":{"inputTokens":1,"outputTokens":50,"thinkingTokens":20,"cacheReadInputTokens":2,"cacheCreationInputTokens":3,"webSearchRequests":0,"costUSD":1.5}}}""",
        """{"type":"system","subtype":"compact_boundary","sessionId":"s1","compactMetadata":{"trigger":"auto","preTokens":1000,"postTokens":100,"durationMs":5000}}""",
        """{"type":"assistant","sessionId":"s1","isApiErrorMessage":true,"error":"rate_limit","apiErrorStatus":429,"quotaLimits":{"status":"rejected","resetsAt":1790342400,"rateLimitType":"five_hour","overageStatus":"disabled","isUsingOverage":false}}""",
    ];

    /// <summary>
    /// The parser shared by the tests.
    /// </summary>
    private readonly SessionParser _parser = new(NullLogger<SessionParser>.Instance);

    /// <summary>
    /// The per-test directory holding the transcript.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aimon-extras-{Guid.NewGuid()}");

    /// <summary>
    /// The transcript file used by the tests.
    /// </summary>
    private readonly string _transcript;

    /// <summary>
    /// Creates the per-test directory.
    /// </summary>
    public SessionRowExtrasTests()
    {
        Directory.CreateDirectory(_dir);
        _transcript = Path.Combine(_dir, "session.jsonl");
    }

    /// <summary>
    /// Removes the per-test directory.
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Verifies that the parser reads every extra field of the assistant and user lines.
    /// </summary>
    [Fact]
    public void ParseFile_ExtraFields_AreParsed()
    {
        File.WriteAllLines(_transcript, [AssistantLine, UserLine]);

        var rows = _parser.ParseFile(_transcript).ToList();

        AssertAssistant(rows[0]);
        AssertUser(rows[1]);
    }

    /// <summary>
    /// Verifies that the metadata lines (titles, pull request, cost snapshot, compaction, rate limit) are parsed.
    /// </summary>
    [Fact]
    public void ParseFile_MetadataLines_AreParsed()
    {
        File.WriteAllLines(_transcript, MetadataLines);

        var rows = _parser.ParseFile(_transcript).ToList();

        AssertMetadata(rows);
    }

    /// <summary>
    /// Verifies that the extra fields survive a serialize/deserialize round trip of the on-disk cache.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_ExtraFields_RoundTrip()
    {
        File.WriteAllLines(_transcript, [AssistantLine, UserLine, .. MetadataLines]);
        var parsed = _parser.ParseFrom(_transcript, 0);
        var entry = new CachedSessionFile(1, 2, parsed.EndOffset, [], parsed.Rows);

        var restored = SessionRowDiskCache.Deserialize(SessionRowDiskCache.Serialize(_transcript, entry), _transcript);

        Assert.NotNull(restored);
        Assert.Equal(parsed.Rows.Count, restored.Rows.Count);
        AssertAssistant(restored.Rows[0]);
        AssertUser(restored.Rows[1]);
        AssertMetadata(restored.Rows.Skip(2).ToList());
    }

    /// <summary>
    /// Verifies that a field of an unexpected JSON type does not cost the line its usage.
    /// </summary>
    [Fact]
    public void ParseFile_FieldsOfUnexpectedType_DoNotDropTheLine()
    {
        File.WriteAllLines(_transcript,
        [
            """{"type":"assistant","uuid":"u1","error":{"x":1},"apiErrorStatus":"500","origin":"odd","thinkingDurationMs":"n/a","message":{"role":"assistant","content":null,"usage":{"input_tokens":5,"output_tokens":6}}}""",
        ]);

        var row = Assert.Single(_parser.ParseFile(_transcript));

        Assert.Null(row.Error);
        Assert.Equal(500, row.ApiErrorStatus);
        Assert.Null(row.Origin);
        Assert.Null(row.ThinkingDurationMs);
        Assert.Equal(0, row.Message!.ToolUseCount);
        Assert.Equal(11, row.Message.Usage!.InputTokens + row.Message.Usage.OutputTokens);
    }

    /// <summary>
    /// Verifies that nested objects and flags of an unexpected shape cost the line neither its usage nor its other fields.
    /// </summary>
    [Fact]
    public void ParseFile_NestedObjectsAndFlagsOfUnexpectedShape_DoNotDropTheLine()
    {
        File.WriteAllLines(_transcript,
        [
            """{"type":"user","uuid":"u1","isSidechain":"yes","isCompactSummary":1,"permissionDecision":"accept","quotaLimits":[1],"compactMetadata":7,"modelUsage":"x","totalCostUSD":"free","message":{"role":"user","content":[{"type":"tool_result","is_error":true}],"diagnostics":"none","usage":{"input_tokens":3,"output_tokens":4,"output_tokens_details":5,"server_tool_use":"n/a"}}}""",
        ]);

        var row = Assert.Single(_parser.ParseFile(_transcript));

        Assert.False(row.IsSidechain);
        Assert.Null(row.PermissionDecision);
        Assert.Null(row.QuotaLimits);
        Assert.Null(row.CompactMetadata);
        Assert.Null(row.ModelUsage);
        Assert.Null(row.TotalCostUsd);
        Assert.Null(row.Message!.Diagnostics);
        Assert.Equal(1, row.Message.Content.ToolResultErrorCount);
        Assert.Equal(7, row.Message.Usage!.InputTokens + row.Message.Usage.OutputTokens);
    }

    /// <summary>
    /// Verifies that fields of the wrong type inside the nested objects, and string fields holding other JSON, are tolerated too.
    /// </summary>
    [Fact]
    public void ParseFile_FieldsOfWrongTypeInsideNestedObjects_DoNotDropTheLine()
    {
        File.WriteAllLines(_transcript,
        [
            """{"type":"assistant","uuid":"u1","gitBranch":42,"effort":{"x":1},"attributionSkill":["a"],"quotaLimits":{"status":3,"resetsAt":"soon","isUsingOverage":"no"},"compactMetadata":{"preTokens":"many","trigger":9},"modelUsage":{"m":{"outputTokens":"lots","costUSD":"free","inputTokens":5}},"message":{"role":"assistant","stop_reason":7,"usage":{"input_tokens":3,"output_tokens":4,"speed":1,"output_tokens_details":{"thinking_tokens":"x"},"server_tool_use":{"web_search_requests":"y"}},"diagnostics":{"cache_miss_reason":{"type":5,"cache_missed_input_tokens":"z"}}}}""",
        ]);

        var row = Assert.Single(_parser.ParseFile(_transcript));

        Assert.Null(row.GitBranch);
        Assert.Null(row.Effort);
        Assert.Null(row.AttributionSkill);
        Assert.Equal((null, null, false), (row.QuotaLimits!.Status, row.QuotaLimits.ResetsAt, row.QuotaLimits.IsUsingOverage));
        Assert.Equal((null, 0), (row.CompactMetadata!.Trigger, row.CompactMetadata.PreTokens));
        Assert.Equal((5, 0, 0d), (row.ModelUsage!["m"].InputTokens, row.ModelUsage["m"].OutputTokens, row.ModelUsage["m"].CostUsd));
        Assert.Null(row.Message!.StopReason);
        Assert.Equal(7, row.Message.Usage!.InputTokens + row.Message.Usage.OutputTokens);
        Assert.Equal(0, row.Message.Usage.ThinkingTokens);
        Assert.Equal(0, row.Message.Usage.ServerToolUse!.WebSearchRequests);
        Assert.Equal(0, row.Message.Diagnostics!.CacheMissReason!.CacheMissedInputTokens);
    }

    /// <summary>
    /// Asserts the extra fields of <see cref="AssistantLine"/>.
    /// </summary>
    /// <param name="row">The parsed row.</param>
    private static void AssertAssistant(Core.Providers.Claude.Models.SessionMessage row)
    {
        Assert.True(row.IsSidechain);
        Assert.Equal(("a1", "main", "cli", "plan"), (row.AgentId, row.GitBranch, row.Entrypoint, row.PermissionMode));
        Assert.Equal(("high", "low", "opus", "sonnet"), (row.Effort, row.PerTurnEffort, row.RequestedModel, row.AdvisorModel));
        Assert.Equal(("brainstorming", "Explore", "srv", "tool", "plug"),
            (row.AttributionSkill, row.AttributionAgent, row.AttributionMcpServer, row.AttributionMcpTool, row.AttributionPlugin));
        Assert.Equal(1234, row.ThinkingDurationMs);
        Assert.Equal("2.1.0", row.Version);

        var message = row.Message!;
        Assert.Equal("tool_use", message.StopReason);
        Assert.Equal(["Bash", "mcp__a__b"], message.Content.ToolNames);
        Assert.Equal(2, message.ToolUseCount);
        Assert.Equal((1, 1, 0, 0), (message.Content.ThinkingBlockCount, message.Content.TextBlockCount,
            message.Content.ToolResultCount, message.Content.ToolResultErrorCount));
        Assert.Equal(("standard", "fast", 20), (message.Usage!.ServiceTier, message.Usage.Speed, message.Usage.ThinkingTokens));
        Assert.Equal((2, 1), (message.Usage.ServerToolUse!.WebSearchRequests, message.Usage.ServerToolUse.WebFetchRequests));
        Assert.Equal(50, message.Usage.OutputTokens);
        Assert.Equal(("system_changed", 999), (message.Diagnostics!.CacheMissReason!.Type, message.Diagnostics.CacheMissReason.CacheMissedInputTokens));
    }

    /// <summary>
    /// Asserts the extra fields of <see cref="UserLine"/>.
    /// </summary>
    /// <param name="row">The parsed row.</param>
    private static void AssertUser(Core.Providers.Claude.Models.SessionMessage row)
    {
        Assert.False(row.IsSidechain);
        Assert.Equal("human", row.Origin!.Kind);
        Assert.Equal("user-rejected", row.ToolDenialKind);
        Assert.Equal(("reject", "user"), (row.PermissionDecision!.Decision, row.PermissionDecision.Source));
        Assert.Equal((2, 1, 0), (row.Message!.Content.ToolResultCount, row.Message.Content.ToolResultErrorCount, row.Message.ToolUseCount));
    }

    /// <summary>
    /// Asserts the contents of <see cref="MetadataLines"/>.
    /// </summary>
    /// <param name="rows">The parsed rows, in the order of <see cref="MetadataLines"/>.</param>
    private static void AssertMetadata(IReadOnlyList<Core.Providers.Claude.Models.SessionMessage> rows)
    {
        Assert.Equal("Fix the bug", rows[0].AiTitle);
        Assert.Equal("My title", rows[1].CustomTitle);
        Assert.Equal("worker", rows[2].AgentName);
        Assert.Equal((42, "https://github.com/o/r/pull/42", "o/r"), (rows[3].PrNumber, rows[3].PrUrl, rows[3].PrRepository));

        var cost = rows[4];
        Assert.Equal(1.5, cost.TotalCostUsd);
        Assert.Equal((100d, 50d, 9000d), (cost.TotalApiDurationMs, cost.TotalToolDurationMs, cost.TotalDurationMs));
        Assert.Equal((10, 2), (cost.TotalLinesAdded, cost.TotalLinesRemoved));
        var usage = Assert.Single(cost.ModelUsage!);
        Assert.Equal("opus", usage.Key);
        Assert.Equal((1, 50, 20, 2, 3), (usage.Value.InputTokens, usage.Value.OutputTokens, usage.Value.ThinkingTokens,
            usage.Value.CacheReadInputTokens, usage.Value.CacheCreationInputTokens));
        Assert.Equal(1.5, usage.Value.CostUsd);

        Assert.Equal("compact_boundary", rows[5].Subtype);
        var compact = rows[5].CompactMetadata!;
        Assert.Equal(("auto", 1000, 100, 5000), (compact.Trigger, compact.PreTokens, compact.PostTokens, compact.DurationMs));

        var error = rows[6];
        Assert.True(error.IsApiErrorMessage);
        Assert.Equal(("rate_limit", 429), (error.Error, error.ApiErrorStatus));
        var quota = error.QuotaLimits!;
        Assert.Equal(("rejected", 1790342400, "five_hour", "disabled", false),
            (quota.Status, quota.ResetsAt, quota.RateLimitType, quota.OverageStatus, quota.IsUsingOverage));
    }
}
