using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for <see cref="SessionUsageBuilder"/>: session identity across transcript files, de-duplication, day placement,
/// model filtering, project attribution and pricing.
/// </summary>
public class SessionUsageBuilderTests : IDisposable
{
    /// <summary>
    /// The directory standing in for <c>~/.claude</c>.
    /// </summary>
    private readonly string _claudeDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    /// <summary>
    /// The cache the builder reads the transcripts through.
    /// </summary>
    private readonly SessionFileCache _cache = new(
        new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance);

    /// <summary>
    /// The prices used to expect costs.
    /// </summary>
    private readonly CostCalculator _costs = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionUsageBuilderTests"/> class.
    /// </summary>
    public SessionUsageBuilderTests()
    {
        Directory.CreateDirectory(ProjectsDir);
    }

    /// <summary>
    /// Gets the directory holding the project folders.
    /// </summary>
    private string ProjectsDir => Path.Combine(_claudeDir, "projects");

    /// <summary>
    /// Deletes the transcript files.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_claudeDir))
        {
            Directory.Delete(_claudeDir, recursive: true);
        }
    }

    /// <summary>
    /// Builds a transcript line.
    /// </summary>
    /// <param name="type">The line type: <c>user</c>, <c>assistant</c> or something else.</param>
    /// <param name="uuid">The line's uuid.</param>
    /// <param name="timestamp">The line's ISO timestamp.</param>
    /// <param name="sessionId">The session id, or <see langword="null"/> to omit it.</param>
    /// <param name="messageId">The API message id of an assistant line; defaults to the uuid.</param>
    /// <param name="model">The model of an assistant line, or <see langword="null"/> for a line without usage.</param>
    /// <param name="input">The input tokens.</param>
    /// <param name="output">The output tokens.</param>
    /// <param name="cacheRead">The cache-read tokens.</param>
    /// <param name="cacheCreation">The cache-creation tokens.</param>
    /// <param name="cwd">The working directory, or <see langword="null"/> to omit it.</param>
    /// <param name="cacheDetail">An optional <c>cache_creation</c> object with the 5-minute and 1-hour split.</param>
    /// <returns>The JSON line.</returns>
    private static string Line(
        string type,
        string uuid,
        string timestamp,
        string? sessionId = "s1",
        string? messageId = null,
        string? model = null,
        int input = 0,
        int output = 0,
        int cacheRead = 0,
        int cacheCreation = 0,
        string? cwd = null,
        string? cacheDetail = null)
    {
        var builder = new StringBuilder();
        builder.Append($"{{\"type\":\"{type}\",\"uuid\":\"{uuid}\",\"timestamp\":\"{timestamp}\"");
        if (sessionId is not null)
        {
            builder.Append($",\"sessionId\":\"{sessionId}\"");
        }

        if (cwd is not null)
        {
            builder.Append($",\"cwd\":\"{cwd}\"");
        }

        if (model is not null)
        {
            var id = messageId ?? uuid;
            builder.Append($",\"requestId\":\"r-{id}\",\"message\":{{\"id\":\"{id}\",\"role\":\"assistant\",\"model\":\"{model}\",\"usage\":{{");
            builder.Append($"\"input_tokens\":{input},\"output_tokens\":{output},\"cache_read_input_tokens\":{cacheRead},\"cache_creation_input_tokens\":{cacheCreation}");
            if (cacheDetail is not null)
            {
                builder.Append($",\"cache_creation\":{cacheDetail}");
            }

            builder.Append("}}");
        }

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// Writes a transcript under a project folder.
    /// </summary>
    /// <param name="relativePath">The path below the projects directory, e.g. <c>projA/s1.jsonl</c>.</param>
    /// <param name="lines">The transcript lines.</param>
    /// <returns>The full path of the written file.</returns>
    private string Write(string relativePath, params string[] lines)
    {
        var path = Path.Combine(ProjectsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    /// <summary>
    /// Builds sessions for a range of days from every transcript written so far.
    /// </summary>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day.</param>
    /// <param name="model">The model filter, if any.</param>
    /// <param name="clock">The clock deciding the local day; the system clock when <see langword="null"/>.</param>
    /// <returns>The built sessions.</returns>
    private List<SessionUsage> Build(DateOnly from, DateOnly to, string? model = null, TimeProvider? clock = null)
    {
        var files = Directory.GetFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
        return new SessionUsageBuilder(_cache, _costs, clock).Build(files, ProjectsDir, from, to, model);
    }

    /// <summary>
    /// Verifies that one session id found in two transcript files is a single session.
    /// </summary>
    [Fact]
    public void Build_SessionSpanningTwoFiles_MergesIntoOneSession()
    {
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"), Line("assistant", "a1", "2026-08-02T10:01:00Z", model: "sonnet-5", input: 10));
        Write("projA/b.jsonl", Line("user", "u2", "2026-08-02T11:00:00Z"), Line("assistant", "a2", "2026-08-02T11:01:00Z", model: "sonnet-5", input: 20));

        var sessions = Build(new(2026, 8, 1), new(2026, 8, 3));

        var session = Assert.Single(sessions);
        Assert.Equal("s1", session.SessionId);
        Assert.Equal(4, session.Messages);
        Assert.Equal(30, session.TotalTokens);
        Assert.Equal(TimeSpan.FromMinutes(61), session.Duration);
    }

    /// <summary>
    /// Verifies that lines copied into a resumed transcript are credited to the session they came from, once.
    /// </summary>
    [Fact]
    public void Build_ResumedTranscriptWithCopiedLines_CountsCopiesOnce()
    {
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z", "s1"), Line("assistant", "a1", "2026-08-02T10:01:00Z", "s1", model: "sonnet-5", input: 10));
        Write("projA/b.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z", "s1"),
            Line("assistant", "a1", "2026-08-02T10:01:00Z", "s1", model: "sonnet-5", input: 10),
            Line("user", "u3", "2026-08-02T12:00:00Z", "s2"));

        var sessions = Build(new(2026, 8, 2), new(2026, 8, 2));

        Assert.Equal(2, sessions.Count);
        Assert.Equal(2, sessions.Single(s => s.SessionId == "s1").Messages);
        Assert.Equal(10, sessions.Single(s => s.SessionId == "s1").TotalTokens);
        Assert.Equal(1, sessions.Single(s => s.SessionId == "s2").Messages);
    }

    /// <summary>
    /// Verifies that the several lines of one streamed response share a single usage.
    /// </summary>
    [Fact]
    public void Build_StreamedResponseWithSameMessageId_CountsUsageOnce()
    {
        Write("projA/a.jsonl",
            Line("assistant", "a1", "2026-08-02T10:00:00Z", messageId: "m1", model: "sonnet-5", input: 100, output: 50),
            Line("assistant", "a2", "2026-08-02T10:00:05Z", messageId: "m1", model: "sonnet-5", input: 100, output: 50));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        Assert.Equal(2, session.Messages);
        Assert.Equal(150, session.TotalTokens);
        Assert.Equal(150, session.TokensByModel["sonnet-5"]);
    }

    /// <summary>
    /// Verifies that a response whose first line is before the range keeps its usage out of the range, as in the daily statistics.
    /// </summary>
    [Fact]
    public void Build_ResponseStraddlingTheStartOfTheRange_AttributesUsageToItsFirstLine()
    {
        var utc = new FixedClock(TimeZoneInfo.Utc);
        Write("projA/a.jsonl",
            Line("assistant", "a1", "2026-08-01T23:59:59Z", messageId: "m1", model: "sonnet-5", input: 100),
            Line("assistant", "a2", "2026-08-02T00:00:01Z", messageId: "m1", model: "sonnet-5", input: 100));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2), clock: utc));

        Assert.Equal(1, session.Messages);
        Assert.Equal(0, session.TotalTokens);
    }

    /// <summary>
    /// Verifies that both end days of the range are included and the days around them are not.
    /// </summary>
    [Fact]
    public void Build_RangeEdges_AreInclusive()
    {
        var utc = new FixedClock(TimeZoneInfo.Utc);
        Write("projA/a.jsonl",
            Line("user", "u0", "2026-07-31T12:00:00Z"),
            Line("user", "u1", "2026-08-01T00:00:00Z"),
            Line("user", "u2", "2026-08-02T23:59:59Z"),
            Line("user", "u3", "2026-08-03T00:00:00Z"));

        var session = Assert.Single(Build(new(2026, 8, 1), new(2026, 8, 2), clock: utc));

        Assert.Equal(2, session.Messages);
    }

    /// <summary>
    /// Verifies that a line is placed on the local day of the injected time zone.
    /// </summary>
    [Fact]
    public void Build_LineNearMidnightUtc_UsesTheInjectedTimeZoneForTheDay()
    {
        var plusEight = new FixedClock(TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8"));
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-01T20:00:00Z"));

        var onAugustSecond = Build(new(2026, 8, 2), new(2026, 8, 2), clock: plusEight);
        var onAugustFirst = Build(new(2026, 8, 1), new(2026, 8, 1), clock: plusEight);

        Assert.Single(onAugustSecond);
        Assert.Empty(onAugustFirst);
    }

    /// <summary>
    /// Verifies that a model filter counts, and prices, only the assistant lines of matching models.
    /// </summary>
    [Fact]
    public void Build_ModelFilter_CountsOnlyMatchingAssistantLines()
    {
        Write("projA/a.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z"),
            Line("assistant", "a1", "2026-08-02T10:01:00Z", model: "claude-opus-5-5", input: 1000, output: 100),
            Line("assistant", "a2", "2026-08-02T10:02:00Z", model: "claude-sonnet-5-5", input: 5000));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2), model: "OPUS"));

        Assert.Equal(1, session.Messages);
        Assert.Equal(1100, session.TotalTokens);
        Assert.Equal(["claude-opus-5-5"], session.TokensByModel.Keys);
        Assert.Equal(_costs.EstimateCost("claude-opus-5-5", 1000, 100, 0, 0, 0), session.EstimatedCost);
    }

    /// <summary>
    /// Verifies that a model filter matching nothing leaves no sessions.
    /// </summary>
    [Fact]
    public void Build_ModelFilterMatchingNothing_ReturnsNoSessions()
    {
        Write("projA/a.jsonl", Line("assistant", "a1", "2026-08-02T10:01:00Z", model: "sonnet-5", input: 10));

        Assert.Empty(Build(new(2026, 8, 2), new(2026, 8, 2), model: "gpt"));
    }

    /// <summary>
    /// Verifies that synthetic lines count as messages but carry no tokens.
    /// </summary>
    [Fact]
    public void Build_SyntheticModel_CountsTheLineButNoTokens()
    {
        Write("projA/a.jsonl", Line("assistant", "a1", "2026-08-02T10:01:00Z", model: "<synthetic>", input: 500));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        Assert.Equal(1, session.Messages);
        Assert.Equal(0, session.TotalTokens);
        Assert.Empty(session.TokensByModel);
    }

    /// <summary>
    /// Verifies that a sub-agent transcript nested below a project folder rolls into its parent session and project.
    /// </summary>
    [Fact]
    public void Build_NestedSubagentTranscript_BelongsToTheParentSessionAndProject()
    {
        Write("projA/s1.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"));
        Write("projA/s1/subagents/agent.jsonl", Line("assistant", "a1", "2026-08-02T10:05:00Z", model: "sonnet-5", input: 40));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        Assert.Equal("projA", session.ProjectKey);
        Assert.Equal(2, session.Messages);
        Assert.Equal(40, session.TotalTokens);
    }

    /// <summary>
    /// Verifies that a session spanning two project folders takes the folder of its earliest line, whatever the folder names sort like.
    /// </summary>
    [Theory]
    [InlineData("projA", "projB")]
    [InlineData("projB", "projA")]
    public void Build_SessionAcrossProjectFolders_PicksTheFolderOfTheEarliestLine(string earlierFolder, string laterFolder)
    {
        Write($"{earlierFolder}/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"));
        Write($"{laterFolder}/b.jsonl", Line("user", "u2", "2026-08-02T11:00:00Z"));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        Assert.Equal(earlierFolder, session.ProjectKey);
    }

    /// <summary>
    /// Verifies that the working directory comes from the earliest line that has one.
    /// </summary>
    [Fact]
    public void Build_WorkingDirectoryChangesDuringTheSession_UsesTheEarliestOne()
    {
        Write("projA/a.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z"),
            Line("user", "u2", "2026-08-02T10:01:00Z", cwd: "C:/repos/app"),
            Line("user", "u3", "2026-08-02T10:02:00Z", cwd: "C:/repos/app/src"));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        Assert.Equal("C:/repos/app", session.ProjectPath);
    }

    /// <summary>
    /// Verifies that a session without any recorded working directory has no project path.
    /// </summary>
    [Fact]
    public void Build_NoWorkingDirectoryRecorded_LeavesTheProjectPathNull()
    {
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"));

        Assert.Null(Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2))).ProjectPath);
    }

    /// <summary>
    /// Verifies that lines without a session id use the first id in their file, or the file name when there is none.
    /// </summary>
    [Fact]
    public void Build_LinesWithoutSessionId_FallBackToTheFileIdentity()
    {
        Write("projA/withid.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z", "known"), Line("user", "u2", "2026-08-02T10:01:00Z", sessionId: null));
        Write("projA/noid.jsonl", Line("user", "u3", "2026-08-02T10:02:00Z", sessionId: null));

        var sessions = Build(new(2026, 8, 2), new(2026, 8, 2));

        Assert.Equal(["known", "noid"], sessions.Select(s => s.SessionId).Order(StringComparer.Ordinal));
        Assert.Equal(2, sessions.Single(s => s.SessionId == "known").Messages);
    }

    /// <summary>
    /// Verifies that lines that are neither user nor assistant messages do not make a session.
    /// </summary>
    [Fact]
    public void Build_OnlyNonMessageLines_ReturnsNoSessions()
    {
        Write("projA/a.jsonl", Line("summary", "x1", "2026-08-02T10:00:00Z"), Line("system", "x2", "2026-08-02T10:01:00Z"));

        Assert.Empty(Build(new(2026, 8, 2), new(2026, 8, 2)));
    }

    /// <summary>
    /// Verifies that the cost is the sum of each model's cost, including the 5-minute and 1-hour cache-write split.
    /// </summary>
    [Fact]
    public void Build_TwoModelsWithCacheWrites_PricesEachModel()
    {
        Write("projA/a.jsonl",
            Line("assistant", "a1", "2026-08-02T10:00:00Z", model: "sonnet-5", input: 1000, output: 200, cacheRead: 3000, cacheCreation: 500,
                cacheDetail: "{\"ephemeral_5m_input_tokens\":200,\"ephemeral_1h_input_tokens\":300}"),
            Line("assistant", "a2", "2026-08-02T10:01:00Z", model: "claude-opus-5-5", input: 10, output: 20));

        var session = Assert.Single(Build(new(2026, 8, 2), new(2026, 8, 2)));

        var expected = _costs.EstimateCost("sonnet-5", 1000, 200, 3000, 200, 300) + _costs.EstimateCost("claude-opus-5-5", 10, 20, 0, 0, 0);
        Assert.Equal(expected, session.EstimatedCost);
        Assert.Equal(4700, session.TokensByModel["sonnet-5"]);
        Assert.Equal(1010, session.InputTokens);
        Assert.Equal(220, session.OutputTokens);
        Assert.Equal(3000, session.CacheReadTokens);
        Assert.Equal(500, session.CacheWriteTokens);
        Assert.Equal(4730, session.TotalTokens);
    }

    /// <summary>
    /// Verifies that a transcript last written long before the range is not even read.
    /// </summary>
    [Fact]
    public void Build_TranscriptNotModifiedSinceTheRange_IsSkipped()
    {
        var path = Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"));
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Empty(Build(new(2026, 8, 2), new(2026, 8, 2)));
    }

    /// <summary>
    /// Verifies that a transcript that cannot be read is ignored rather than failing the whole build.
    /// </summary>
    [Fact]
    public void Build_MissingTranscript_IsIgnored()
    {
        var missing = Path.Combine(ProjectsDir, "projA", "gone.jsonl");

        var sessions = new SessionUsageBuilder(_cache, _costs).Build([missing], ProjectsDir, new(2026, 8, 2), new(2026, 8, 2));

        Assert.Empty(sessions);
    }

    /// <summary>
    /// Verifies that lines copied into a resumed transcript in another project folder stay credited to the original's folder,
    /// whichever folder name sorts first and whichever transcript was written last.
    /// </summary>
    [Theory]
    [InlineData("projC", "projB", false)]
    [InlineData("projB", "projC", false)]
    [InlineData("projC", "projB", true)]
    [InlineData("projB", "projC", true)]
    public void Build_ResumedInAnotherProjectFolder_KeepsTheOriginalProject(string originalFolder, string resumedFolder, bool originalWrittenLast)
    {
        var original = Write($"{originalFolder}/s1.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z", "s1", cwd: "C:/original"),
            Line("user", "u2", "2026-08-02T10:05:00Z", "s1", cwd: "C:/original"));
        var resumed = Write($"{resumedFolder}/s2.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z", "s1", cwd: "C:/original"),
            Line("user", "u2", "2026-08-02T10:05:00Z", "s1", cwd: "C:/original"),
            Line("user", "u3", "2026-08-03T09:00:00Z", "s2", cwd: "C:/resumed"));
        File.SetLastWriteTimeUtc(original, new DateTime(2026, 8, originalWrittenLast ? 5 : 4, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(resumed, new DateTime(2026, 8, originalWrittenLast ? 4 : 5, 0, 0, 0, DateTimeKind.Utc));

        var sessions = Build(new(2026, 8, 1), new(2026, 8, 5));

        var first = sessions.Single(s => s.SessionId == "s1");
        Assert.Equal(originalFolder, first.ProjectKey);
        Assert.Equal("C:/original", first.ProjectPath);
        Assert.Equal(2, first.Messages);
        Assert.Equal(resumedFolder, sessions.Single(s => s.SessionId == "s2").ProjectKey);
    }

    /// <summary>
    /// Verifies that copied lines are still counted once, in the copy's folder, when the original transcript is not there.
    /// </summary>
    [Fact]
    public void Build_CopiedLinesWithoutTheOriginalTranscript_AreCountedFromTheCopy()
    {
        Write("projB/s2.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z", "s1"),
            Line("user", "u2", "2026-08-03T09:00:00Z", "s2"));

        var sessions = Build(new(2026, 8, 1), new(2026, 8, 5));

        Assert.Equal(["s1", "s2"], sessions.Select(s => s.SessionId));
        Assert.All(sessions, s => Assert.Equal("projB", s.ProjectKey));
    }

    /// <summary>
    /// Verifies that the time zone is read on every build, not once when the builder is created.
    /// </summary>
    [Fact]
    public void Build_TimeZoneChangesBetweenCalls_UsesTheCurrentZone()
    {
        var clock = new ChangingClock { Zone = TimeZoneInfo.Utc };
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-01T20:00:00Z"));
        var builder = new SessionUsageBuilder(_cache, _costs, clock);
        var files = Directory.GetFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories);

        var inUtc = builder.Build(files, ProjectsDir, new(2026, 8, 2), new(2026, 8, 2));
        clock.Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var inPlusEight = builder.Build(files, ProjectsDir, new(2026, 8, 2), new(2026, 8, 2));

        Assert.Empty(inUtc);
        Assert.Single(inPlusEight);
    }

    /// <summary>
    /// Verifies that a range from the first to the last representable day means everything instead of overflowing.
    /// </summary>
    [Fact]
    public void Build_UnboundedRange_ReturnsEverythingWithoutOverflowing()
    {
        Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z"));

        var session = Assert.Single(Build(DateOnly.MinValue, DateOnly.MaxValue));

        Assert.Equal(1, session.Messages);
    }

    /// <summary>
    /// Verifies that the result does not depend on the order the transcripts are listed in.
    /// </summary>
    [Fact]
    public void Build_FilesInDifferentOrder_GivesTheSameSessions()
    {
        var first = Write("projA/a.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z", "s1"), Line("user", "u2", "2026-08-02T10:01:00Z", "s2"));
        var second = Write("projB/b.jsonl", Line("user", "u1", "2026-08-02T10:00:00Z", "s1"), Line("user", "u3", "2026-08-02T10:02:00Z", "s3"));
        var builder = new SessionUsageBuilder(_cache, _costs);

        var forward = builder.Build([first, second], ProjectsDir, new(2026, 8, 2), new(2026, 8, 2));
        var backward = builder.Build([second, first], ProjectsDir, new(2026, 8, 2), new(2026, 8, 2));

        Assert.Equal(forward.Select(s => (s.SessionId, s.ProjectKey, s.Messages)), backward.Select(s => (s.SessionId, s.ProjectKey, s.Messages)));
    }

    /// <summary>
    /// Verifies that the tokens over a whole range add up to what the daily statistics report for the same transcripts.
    /// </summary>
    [Fact]
    public void Build_TotalsOverTheWholeRange_MatchTheDailyStatistics()
    {
        Write("projA/a.jsonl",
            Line("user", "u1", "2026-08-02T10:00:00Z", "s1"),
            Line("assistant", "a1", "2026-08-02T10:01:00Z", "s1", messageId: "m1", model: "sonnet-5", input: 100, output: 10, cacheRead: 5, cacheCreation: 7),
            Line("assistant", "a2", "2026-08-02T10:01:02Z", "s1", messageId: "m1", model: "sonnet-5", input: 100, output: 10, cacheRead: 5, cacheCreation: 7),
            Line("assistant", "a3", "2026-08-05T10:00:00Z", "s2", model: "claude-opus-5-5", input: 30));
        Write("projB/b.jsonl",
            Line("assistant", "a1", "2026-08-02T10:01:00Z", "s1", messageId: "m1", model: "sonnet-5", input: 100, output: 10, cacheRead: 5, cacheCreation: 7),
            Line("assistant", "a4", "2026-08-06T09:00:00Z", "s3", model: "sonnet-5", input: 11));
        var files = Directory.GetFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
        var stats = new StatsCacheBuilder(_cache).Build(files);

        var sessions = Build(new(2026, 8, 1), new(2026, 8, 31));

        Assert.Equal(stats.DailyModelTokens.Sum(d => d.TokensByModel.Values.Sum()), sessions.Sum(s => s.TotalTokens));
    }

    /// <summary>
    /// Verifies the project folder of transcripts directly below, deeper below and outside the projects directory.
    /// </summary>
    [Fact]
    public void ProjectFolder_VariousLocations_ReturnsTheFirstFolderBelowTheProjectsDirectory()
    {
        Assert.Equal("projA", SessionUsageBuilder.ProjectFolder(ProjectsDir, Path.Combine(ProjectsDir, "projA", "s.jsonl")));
        Assert.Equal("projA", SessionUsageBuilder.ProjectFolder(ProjectsDir, Path.Combine(ProjectsDir, "projA", "s", "subagents", "x.jsonl")));
        Assert.Equal("elsewhere", SessionUsageBuilder.ProjectFolder(ProjectsDir, Path.Combine(_claudeDir, "elsewhere", "s.jsonl")));
    }

    /// <summary>
    /// A clock whose local time zone can be changed between calls.
    /// </summary>
    private sealed class ChangingClock : TimeProvider
    {
        /// <summary>
        /// Gets or sets the local time zone to report.
        /// </summary>
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    /// <summary>
    /// A clock that decides the local time zone and otherwise follows the system clock.
    /// </summary>
    /// <param name="zone">The local time zone to report.</param>
    private sealed class FixedClock(TimeZoneInfo zone) : TimeProvider
    {
        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
