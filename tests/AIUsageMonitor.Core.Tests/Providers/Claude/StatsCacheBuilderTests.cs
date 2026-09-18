using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class StatsCacheBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");
    private readonly StatsCacheBuilder _sut = new(new SessionFileCache(
        new SessionParser(NullLogger<SessionParser>.Instance),
        NullLogger<SessionFileCache>.Instance));

    public StatsCacheBuilderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteSessionFile(string name, params string[] lines)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Build_NoFiles_ReturnsEmptyCache()
    {
        var cache = _sut.Build([]);

        Assert.Empty(cache.DailyActivity);
        Assert.Empty(cache.ModelUsage);
        Assert.Equal(0, cache.TotalMessages);
        Assert.Equal(0, cache.TotalSessions);
        Assert.Null(cache.LongestSession);
    }

    [Fact]
    public void Build_AggregatesDailyActivityAndTokens()
    {
        var file = WriteSessionFile("s1.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:05:00Z","sessionId":"s1","message":{"role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":0}}}""");

        var cache = _sut.Build([file]);

        var day = Assert.Single(cache.DailyActivity);
        Assert.Equal(new DateOnly(2026, 8, 1), day.Date);
        Assert.Equal(2, day.MessageCount);
        Assert.Equal(1, day.SessionCount);
        Assert.Equal(1, cache.TotalSessions);
        Assert.Equal(2, cache.TotalMessages);

        var usage = Assert.Single(cache.ModelUsage);
        Assert.Equal("sonnet-5", usage.Key);
        Assert.Equal(100, usage.Value.InputTokens);
        Assert.Equal(50, usage.Value.OutputTokens);

        var modelTokens = Assert.Single(cache.DailyModelTokens);
        Assert.Equal(160, modelTokens.TokensByModel["sonnet-5"]);
    }

    [Fact]
    public void Build_DuplicateAssistantMessage_CountsTokensOnce()
    {
        var file = WriteSessionFile("s1.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:00Z","sessionId":"s1","requestId":"req1","message":{"id":"msg1","role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:05Z","sessionId":"s1","requestId":"req1","message":{"id":"msg1","role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""");

        var cache = _sut.Build([file]);

        var usage = cache.ModelUsage["sonnet-5"];
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
    }

    [Fact]
    public void Build_SyntheticModel_IsExcludedFromModelUsage()
    {
        var file = WriteSessionFile("s1.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:00Z","sessionId":"s1","message":{"role":"assistant","model":"<synthetic>","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""");

        var cache = _sut.Build([file]);

        Assert.Empty(cache.ModelUsage);
    }

    [Fact]
    public void Build_TracksLongestSessionAcrossMultipleFiles()
    {
        var shortFile = WriteSessionFile("short.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"short"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:00Z","sessionId":"short"}""");
        var longFile = WriteSessionFile("long.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"long"}""",
            """{"type":"assistant","timestamp":"2026-08-01T01:00:00Z","sessionId":"long"}""");

        var cache = _sut.Build([shortFile, longFile]);

        Assert.NotNull(cache.LongestSession);
        Assert.Equal("long", cache.LongestSession.SessionId);
        Assert.Equal(TimeSpan.FromHours(1).TotalMilliseconds, cache.LongestSession.Duration);
    }

    [Fact]
    public void Build_UnreadableFile_IsSkippedWithoutThrowing()
    {
        var missingFile = Path.Combine(_tempDir, "missing.jsonl");

        var cache = _sut.Build([missingFile]);

        Assert.Empty(cache.DailyActivity);
    }

    [Fact]
    public void Build_ReportsProgressForEachFile()
    {
        var file1 = WriteSessionFile("s1.jsonl", """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""");
        var file2 = WriteSessionFile("s2.jsonl", """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s2"}""");
        var reported = new List<int>();
        var progress = new SynchronousProgress<int>(reported.Add);

        _sut.Build([file1, file2], progress);

        Assert.Equal([50, 100], reported);
    }

    [Fact]
    public void Build_FallsBackToFileNameWhenNoSessionId()
    {
        var file = WriteSessionFile("no-session-id.jsonl",
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z"}""");

        var cache = _sut.Build([file]);

        Assert.Equal(1, cache.TotalSessions);
    }

    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
