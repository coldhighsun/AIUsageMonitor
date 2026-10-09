using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Analytics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests that resumed-session copies are counted once across transcript files, both for the
/// <see cref="TranscriptDeduplicator"/> itself and for the builders that use it.
/// </summary>
public class TranscriptDeduplicatorTests : IDisposable
{
    /// <summary>
    /// The directory the transcript files of a test are written to.
    /// </summary>
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    /// <summary>
    /// The cache the builders read the transcripts through.
    /// </summary>
    private readonly SessionFileCache _cache = new(
        new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance);

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscriptDeduplicatorTests"/> class.
    /// </summary>
    public TranscriptDeduplicatorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>
    /// Deletes the transcript files.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Builds an assistant transcript line.
    /// </summary>
    /// <param name="uuid">The line's uuid.</param>
    /// <param name="timestamp">The line's timestamp.</param>
    /// <param name="messageId">The API message id.</param>
    /// <param name="inputTokens">The input tokens reported.</param>
    /// <returns>The JSON line.</returns>
    private static string AssistantLine(string uuid, DateTimeOffset timestamp, string messageId, int inputTokens)
    {
        return "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + timestamp.ToString("O")
            + "\",\"sessionId\":\"s\",\"requestId\":\"r-" + messageId + "\",\"message\":{\"id\":\"" + messageId
            + "\",\"role\":\"assistant\",\"model\":\"sonnet-5\",\"usage\":{\"input_tokens\":" + inputTokens
            + ",\"output_tokens\":0,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";
    }

    /// <summary>
    /// Writes a transcript file.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="lines">The lines to write.</param>
    /// <returns>The full path of the file.</returns>
    private string WriteFile(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, lines);

        return path;
    }

    /// <summary>
    /// Verifies that the second line with the same uuid is reported as a repeat.
    /// </summary>
    [Fact]
    public void TryAddLine_SameUuidTwice_RejectsTheSecondCall()
    {
        var sut = new TranscriptDeduplicator();
        var line = new SessionMessage { Uuid = "u1" };

        var first = sut.TryAddLine(line);
        var second = sut.TryAddLine(new SessionMessage { Uuid = "u1" });

        Assert.True(first);
        Assert.False(second);
    }

    /// <summary>
    /// Verifies that lines without a uuid are never treated as repeats of each other.
    /// </summary>
    [Fact]
    public void TryAddLine_NoUuid_AlwaysAccepts()
    {
        var sut = new TranscriptDeduplicator();

        Assert.True(sut.TryAddLine(new SessionMessage()));
        Assert.True(sut.TryAddLine(new SessionMessage()));
    }

    /// <summary>
    /// Verifies that stats built from a session and the transcript it was resumed into count the copied lines once.
    /// </summary>
    [Fact]
    public void Build_StatsCacheWithResumedCopyInSecondFile_CountsCopiedLinesOnce()
    {
        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var original = AssistantLine("u1", at, "m1", 100);
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original, AssistantLine("u2", at.AddMinutes(1), "m2", 20));

        var stats = new StatsCacheBuilder(_cache).Build([first, second]);

        Assert.Equal(2, stats.TotalMessages);
        Assert.Equal(120, stats.ModelUsage["sonnet-5"].InputTokens);
    }

    /// <summary>
    /// Verifies that the transcript a session was resumed into counts as a session of its own, not as the original one.
    /// </summary>
    [Fact]
    public void Build_StatsCacheWithResumedSession_CountsOriginalAndResumedSessionSeparately()
    {
        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var original = AssistantLine("u1", at, "m1", 100);
        var resumedOnly = AssistantLine("u2", at.AddMinutes(1), "m2", 20).Replace("\"sessionId\":\"s\"", "\"sessionId\":\"s-resumed\"");
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original, resumedOnly);

        var stats = new StatsCacheBuilder(_cache).Build([first, second]);

        Assert.Equal(2, stats.TotalSessions);
        Assert.Equal(2, Assert.Single(stats.DailyActivity).SessionCount);
    }

    /// <summary>
    /// Verifies that the longest-session figure measures each session over its own lines, whichever of the two
    /// transcripts is read first, instead of crediting the copied history to the resumed transcript.
    /// </summary>
    /// <param name="resumedFirst">Whether the resumed transcript is listed before the original one.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_StatsCacheWithResumedSession_MeasuresLongestSessionOverItsOwnLines(bool resumedFirst)
    {
        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var early = AssistantLine("u1", at, "m1", 100);
        var late = AssistantLine("u2", at.AddHours(1), "m2", 100);
        var resumedOnly = AssistantLine("u3", at.AddHours(1).AddMinutes(5), "m3", 20).Replace("\"sessionId\":\"s\"", "\"sessionId\":\"s-resumed\"");
        var original = WriteFile("a.jsonl", early, late);
        var resumed = WriteFile("b.jsonl", early, late, resumedOnly);

        var stats = new StatsCacheBuilder(_cache).Build(resumedFirst ? [resumed, original] : [original, resumed]);

        Assert.NotNull(stats.LongestSession);
        Assert.Equal("s", stats.LongestSession.SessionId);
        Assert.Equal(TimeSpan.FromHours(1).TotalMilliseconds, stats.LongestSession.Duration);
        Assert.Equal(2, stats.LongestSession.MessageCount);
    }

    /// <summary>
    /// Verifies that a session seen only on non-message lines is never reported as the longest session.
    /// </summary>
    [Fact]
    public void Build_StatsCacheWithSessionThatHasNoMessages_IgnoresItForLongestSession()
    {
        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        string SystemLine(string uuid, DateTimeOffset timestamp) =>
            "{\"type\":\"system\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + timestamp.ToString("O") + "\",\"sessionId\":\"sys\"}";
        var file = WriteFile(
            "a.jsonl",
            SystemLine("x1", at),
            SystemLine("x2", at.AddHours(3)),
            AssistantLine("u1", at.AddHours(1), "m1", 10),
            AssistantLine("u2", at.AddHours(1).AddMinutes(10), "m2", 10));

        var stats = new StatsCacheBuilder(_cache).Build([file]);

        Assert.NotNull(stats.LongestSession);
        Assert.Equal("s", stats.LongestSession.SessionId);
        Assert.Equal(TimeSpan.FromMinutes(10).TotalMilliseconds, stats.LongestSession.Duration);
    }

    /// <summary>
    /// Verifies that the recent-activity session count treats the transcript a session was resumed into as a session of its own.
    /// </summary>
    [Fact]
    public void Build_RecentActivityWithResumedSession_CountsOriginalAndResumedSessionSeparately()
    {
        var at = DateTimeOffset.Now.AddMinutes(-10);
        var original = AssistantLine("u1", at, "m1", 100);
        var resumedOnly = AssistantLine("u2", at.AddMinutes(1), "m2", 20).Replace("\"sessionId\":\"s\"", "\"sessionId\":\"s-resumed\"");
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original, resumedOnly);

        var recent = new RecentActivityBuilder(_cache, new CostCalculator()).Build([first, second], TimeSpan.FromHours(1));

        Assert.Equal(2, recent.Sessions);
        Assert.Equal(2, recent.Messages);
    }

    /// <summary>
    /// Verifies that hourly token totals count a response copied into a resumed transcript once.
    /// </summary>
    [Fact]
    public void Build_HourlyActivityWithResumedCopyInSecondFile_CountsTokensOnce()
    {
        var at = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var original = AssistantLine("u1", at, "m1", 100);
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original);

        var hours = new HourlyActivityBuilder(_cache).Build([first, second]);

        Assert.Equal(100, hours.Sum(h => h.TotalTokens));
    }

    /// <summary>
    /// Verifies that the recent-activity totals count lines and tokens copied into a resumed transcript once.
    /// </summary>
    [Fact]
    public void Build_RecentActivityWithResumedCopyInSecondFile_CountsMessagesAndTokensOnce()
    {
        var at = DateTimeOffset.Now.AddMinutes(-5);
        var original = AssistantLine("u1", at, "m1", 100);
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original);

        var recent = new RecentActivityBuilder(_cache, new CostCalculator()).Build([first, second], TimeSpan.FromHours(1));

        Assert.Equal(1, recent.Messages);
        Assert.Equal(100, recent.TotalTokens);
    }

    /// <summary>
    /// Verifies that a usage window counts a line copied into a resumed transcript once.
    /// </summary>
    [Fact]
    public void BuildWeekWindow_ResumedCopyInSecondFile_CountsMessagesAndTokensOnce()
    {
        var at = DateTimeOffset.Now.AddMinutes(-5);
        var original = AssistantLine("u1", at, "m1", 100);
        var first = WriteFile("a.jsonl", original);
        var second = WriteFile("b.jsonl", original);

        var window = new SessionBlockBuilder(_cache, new CostCalculator()).BuildWeekWindow([first, second], null);

        Assert.Equal(1, window.Messages);
        Assert.Equal(100, window.TotalTokens);
    }
}
