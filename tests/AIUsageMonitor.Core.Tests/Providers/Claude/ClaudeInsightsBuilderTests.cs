using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for <see cref="ClaudeInsightsBuilder"/>: attribution, tool use, cache and thinking figures, compactions,
/// rate limits, cost checks and session metadata.
/// </summary>
public sealed class ClaudeInsightsBuilderTests : IDisposable
{
    /// <summary>
    /// The directory standing in for <c>~/.claude</c>.
    /// </summary>
    private readonly string _claudeDir = Path.Combine(Path.GetTempPath(), $"claude-insights-{Guid.NewGuid()}");

    /// <summary>
    /// The first day of the range the tests read.
    /// </summary>
    private static readonly DateOnly From = new(2026, 8, 1);

    /// <summary>
    /// The last day of the range the tests read.
    /// </summary>
    private static readonly DateOnly To = new(2026, 8, 5);

    /// <summary>
    /// Creates the directory that stands in for the projects folder.
    /// </summary>
    public ClaudeInsightsBuilderTests()
    {
        Directory.CreateDirectory(Path.Combine(_claudeDir, "projects", "p"));
    }

    /// <summary>
    /// Deletes the transcripts.
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(_claudeDir, recursive: true);
    }

    /// <summary>
    /// Builds an assistant line with usage and the given extra top-level fields.
    /// </summary>
    /// <param name="uuid">The uuid; also the message id.</param>
    /// <param name="timestamp">The ISO timestamp.</param>
    /// <param name="extra">Extra top-level JSON members (with a leading comma), e.g. attribution.</param>
    /// <param name="content">The JSON array of content blocks.</param>
    /// <param name="usage">The inside of the usage object.</param>
    /// <param name="model">The model name.</param>
    /// <param name="diagnostics">An optional <c>diagnostics</c> member (with a leading comma) of the message.</param>
    /// <returns>The JSON line.</returns>
    private static string Assistant(
        string uuid,
        string timestamp,
        string extra = "",
        string content = "[]",
        string usage = "\"input_tokens\":1000000,\"output_tokens\":100000",
        string model = "claude-sonnet-5",
        string diagnostics = "")
    {
        return $$$"""{"type":"assistant","uuid":"{{{uuid}}}","timestamp":"{{{timestamp}}}","sessionId":"s1","requestId":"r-{{{uuid}}}"{{{extra}}},"message":{"id":"{{{uuid}}}","role":"assistant","model":"{{{model}}}","content":{{{content}}},"usage":{ {{{usage}}} }{{{diagnostics}}}}}""";
    }

    /// <summary>
    /// Writes a transcript under the projects folder.
    /// </summary>
    /// <param name="lines">The lines.</param>
    private void Write(params string[] lines)
    {
        File.WriteAllLines(Path.Combine(_claudeDir, "projects", "p", "s1.jsonl"), lines);
    }

    /// <summary>
    /// Builds the report for the default range.
    /// </summary>
    /// <returns>The report.</returns>
    private InsightsReport Build()
    {
        var cache = new SessionFileCache(new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance);

        return new ClaudeInsightsBuilder(new ClaudeDataLocator(_claudeDir), cache, new CostCalculator()).Build(From, To);
    }

    /// <summary>
    /// Verifies that responses are split between the main conversation and sub-agents and by skill, and priced.
    /// </summary>
    [Fact]
    public void Build_SplitsUsageBySidechainAndSkill()
    {
        Write(
            Assistant("a1", "2026-08-02T10:00:00Z", extra: ",\"attributionSkill\":\"brainstorming\",\"gitBranch\":\"main\",\"effort\":\"high\""),
            Assistant("a2", "2026-08-02T10:01:00Z", extra: ",\"isSidechain\":true,\"attributionAgent\":\"Explore\",\"gitBranch\":\"main\""));

        var report = Build();

        Assert.Equal(2, report.Totals.Responses);
        Assert.Equal(6m, report.Totals.EstimatedCost);
        Assert.Equal(["main", "subagent"], report.Attribution["kind"].Select(r => r.Key));
        Assert.Equal(3m, report.Attribution["kind"].Single(r => r.Key == "subagent").EstimatedCost);
        Assert.Equal(1, report.Attribution["skill"].Single(r => r.Key == "brainstorming").Responses);
        Assert.Equal("Explore", Assert.Single(report.Attribution["agent"]).Key);
        Assert.Equal(2, report.Attribution["branch"].Single(r => r.Key == "main").Responses);
        Assert.Equal("high", Assert.Single(report.Attribution["effort"]).Key);
        Assert.Equal(1, report.Sessions.Single().SubagentResponses);
    }

    /// <summary>
    /// Verifies that a streamed response written as several lines counts once, and synthetic lines are ignored.
    /// </summary>
    [Fact]
    public void Build_StreamedResponseAndSyntheticModel_AreCountedOnce()
    {
        var first = Assistant("a1", "2026-08-02T10:00:00Z");
        var secondLine = first.Replace("\"uuid\":\"a1\"", "\"uuid\":\"a1b\"", StringComparison.Ordinal);
        Write(first, secondLine, Assistant("a3", "2026-08-02T10:02:00Z", model: "<synthetic>"));

        var report = Build();

        Assert.Equal(1, report.Totals.Responses);
        Assert.Equal(3m, report.Totals.EstimatedCost);
    }

    /// <summary>
    /// Verifies that lines outside the requested days are left out.
    /// </summary>
    [Fact]
    public void Build_LinesOutsideTheRange_AreIgnored()
    {
        Write(Assistant("a1", "2026-08-02T10:00:00Z"), Assistant("a2", "2026-07-01T10:00:00Z"), Assistant("a3", "2026-09-20T10:00:00Z"));

        Assert.Equal(1, Build().Totals.Responses);
    }

    /// <summary>
    /// Verifies the tool counts, tool results, errors, denials and permission decisions.
    /// </summary>
    [Fact]
    public void Build_CountsToolsResultsErrorsAndPermissionDecisions()
    {
        Write(
            Assistant("a1", "2026-08-02T10:00:00Z", content: """[{"type":"tool_use","name":"Bash"},{"type":"tool_use","name":"mcp__srv__do"},{"type":"tool_use","name":"Bash"}]"""),
            """{"type":"user","uuid":"u1","timestamp":"2026-08-02T10:00:05Z","sessionId":"s1","toolDenialKind":"user-rejected","permissionDecision":{"decision":"reject","source":"user"},"message":{"role":"user","content":[{"type":"tool_result","is_error":true},{"type":"tool_result"},{"type":"tool_result"}]}}""");

        var report = Build();

        Assert.Equal(["Bash", "mcp__srv__do"], report.Tools.Select(t => t.Name));
        Assert.Equal(2, report.Tools[0].Calls);
        Assert.Null(report.Tools[0].McpServer);
        Assert.Equal("srv", report.Tools[1].McpServer);
        Assert.Equal((3, 3, 1), (report.ToolSummary.ToolCalls, report.ToolSummary.ToolResults, report.ToolSummary.ToolErrors));
        Assert.Equal(1, report.ToolSummary.Denials["user-rejected"]);
        Assert.Equal(1, report.ToolSummary.Decisions["reject/user"]);
    }

    /// <summary>
    /// Verifies the cache hit rate, thinking share, fast responses and cache-miss reasons.
    /// </summary>
    [Fact]
    public void Build_ComputesCacheAndThinkingFigures()
    {
        Write(Assistant(
            "a1",
            "2026-08-02T10:00:00Z",
            usage: "\"input_tokens\":100,\"output_tokens\":200,\"cache_read_input_tokens\":700,\"cache_creation_input_tokens\":200,\"cache_creation\":{\"ephemeral_5m_input_tokens\":50,\"ephemeral_1h_input_tokens\":150},\"speed\":\"fast\",\"output_tokens_details\":{\"thinking_tokens\":50}",
            diagnostics: ",\"diagnostics\":{\"cache_miss_reason\":{\"type\":\"tools_changed\",\"cache_missed_input_tokens\":900}}"));

        var cache = Build().Cache;

        Assert.Equal(0.7, cache.HitRate, 3);
        Assert.Equal((50, 150), (cache.CacheWrite5mTokens, cache.CacheWrite1hTokens));
        Assert.Equal((50, 0.25, 1, 1), (cache.ThinkingTokens, cache.ThinkingShare, cache.ThinkingResponses, cache.FastResponses));
        var miss = Assert.Single(cache.MissReasons);
        Assert.Equal(("tools_changed", 1, 900), (miss.Reason, miss.Count, miss.MissedInputTokens));
    }

    /// <summary>
    /// Verifies that compactions and rate-limit rejections are reported with their times.
    /// </summary>
    [Fact]
    public void Build_ReportsCompactionsAndRateLimits()
    {
        Write(
            """{"type":"system","subtype":"compact_boundary","uuid":"c1","timestamp":"2026-08-02T10:00:00Z","sessionId":"s1","compactMetadata":{"trigger":"auto","preTokens":900,"postTokens":90,"durationMs":5}}""",
            """{"type":"assistant","uuid":"e1","timestamp":"2026-08-02T11:00:00Z","sessionId":"s1","isApiErrorMessage":true,"quotaLimits":{"status":"rejected","resetsAt":1785600000,"rateLimitType":"five_hour","overageStatus":"disabled","isUsingOverage":false}}""");

        var report = Build();

        var compaction = Assert.Single(report.Compactions);
        Assert.Equal(("auto", 900, 90), (compaction.Trigger, compaction.PreTokens, compaction.PostTokens));
        var limit = Assert.Single(report.RateLimits);
        Assert.Equal("five_hour", limit.LimitType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785600000), limit.ResetsAt);
    }

    /// <summary>
    /// Verifies that Claude Code's own cost figure is set next to the estimate, and left out when the session reaches outside the range.
    /// </summary>
    [Fact]
    public void Build_CostCheck_ComparesClaudeCostWithEstimate_AndSkipsPartialSessions()
    {
        const string costState = """{"type":"cost-state","sessionId":"s1","totalCostUSD":2.5,"totalAPIDuration":10,"totalToolDuration":5,"totalLinesAdded":7,"totalLinesRemoved":1,"totalDuration":99,"startTime":1785628800000}""";
        Write(Assistant("a1", "2026-08-02T10:00:00Z"), costState);

        var check = Assert.Single(Build().CostChecks);
        Assert.Equal((2.5m, 3m, 7, 1), (check.ClaudeCostUsd, check.EstimatedCostUsd, check.LinesAdded, check.LinesRemoved));

        Write(Assistant("a1", "2026-08-02T10:00:00Z"), Assistant("a2", "2026-08-20T10:00:00Z"), costState);

        Assert.Empty(Build().CostChecks);
    }

    /// <summary>
    /// Verifies that the cost snapshot used is the one with the longest duration, whichever transcript is read last.
    /// </summary>
    [Fact]
    public void Build_CostCheck_UsesTheLatestSnapshotNotTheLastFileRead()
    {
        File.WriteAllLines(Path.Combine(_claudeDir, "projects", "p", "a-old.jsonl"),
        [
            Assistant("a1", "2026-08-02T10:00:00Z"),
            """{"type":"cost-state","sessionId":"s1","totalCostUSD":9,"totalDuration":5000,"startTime":1785628800000}""",
        ]);
        File.WriteAllLines(Path.Combine(_claudeDir, "projects", "p", "z-new.jsonl"),
        [
            """{"type":"cost-state","sessionId":"s1","totalCostUSD":1,"totalDuration":100,"startTime":1785628800000}""",
        ]);

        Assert.Equal(9m, Assert.Single(Build().CostChecks).ClaudeCostUsd);
    }

    /// <summary>
    /// Verifies that a session that started before the transcripts that were read gets no cost check.
    /// </summary>
    [Fact]
    public void Build_CostCheck_SessionStartedBeforeTheReadTranscripts_IsLeftOut()
    {
        Write(
            Assistant("a1", "2026-08-02T10:00:00Z"),
            """{"type":"cost-state","sessionId":"s1","totalCostUSD":2.5,"totalDuration":99,"startTime":1780000000000}""");

        var report = Build();

        Assert.Empty(report.CostChecks);
        Assert.Equal(1, report.CostChecksExcluded);
    }

    /// <summary>
    /// Verifies that lines without a session id are credited to the session of their transcript.
    /// </summary>
    [Fact]
    public void Build_LineWithoutSessionId_TakesTheSessionOfItsTranscript()
    {
        Write(
            Assistant("a1", "2026-08-02T10:00:00Z", extra: ",\"isSidechain\":true"),
            Assistant("a2", "2026-08-02T10:01:00Z", extra: ",\"isSidechain\":true").Replace("\"sessionId\":\"s1\",", string.Empty, StringComparison.Ordinal));

        var session = Assert.Single(Build().Sessions);

        Assert.Equal(("s1", 2), (session.SessionId, session.SubagentResponses));
    }

    /// <summary>
    /// Verifies the session titles, agent name, branch, entry point and pull requests.
    /// </summary>
    [Fact]
    public void Build_ReportsSessionMetadata()
    {
        Write(
            Assistant("a1", "2026-08-02T10:00:00Z", extra: ",\"gitBranch\":\"feat\",\"entrypoint\":\"cli\",\"version\":\"2.0.0\""),
            """{"type":"ai-title","sessionId":"s1","aiTitle":"Generated"}""",
            """{"type":"custom-title","sessionId":"s1","customTitle":"Chosen"}""",
            """{"type":"agent-name","sessionId":"s1","agentName":"worker"}""",
            """{"type":"pr-link","sessionId":"s1","timestamp":"2026-08-02T12:00:00Z","prNumber":7,"prUrl":"https://github.com/o/r/pull/7","prRepository":"o/r"}""");

        var session = Assert.Single(Build().Sessions);

        Assert.Equal(("Chosen", "worker", "feat", "cli", "2.0.0"), (session.Title, session.AgentName, session.Branch, session.Entrypoint, session.Version));
        Assert.Equal(["https://github.com/o/r/pull/7"], session.PullRequests);
    }

    /// <summary>
    /// Verifies how the server of an MCP tool is read from its name.
    /// </summary>
    /// <param name="tool">The tool name.</param>
    /// <param name="server">The expected server, or <see langword="null"/> for a built-in tool.</param>
    [Theory]
    [InlineData("Bash", null)]
    [InlineData("mcp__srv__tool", "srv")]
    [InlineData("mcp__srv", "srv")]
    [InlineData("mcp__a__b__c", "a")]
    public void McpServerOf_ReadsTheServerName(string tool, string? server)
    {
        Assert.Equal(server, ClaudeInsightsBuilder.McpServerOf(tool));
    }
}
