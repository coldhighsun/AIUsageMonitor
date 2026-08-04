using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class SessionParserTests : IDisposable
{
    private readonly SessionParser _sut = new(NullLogger<SessionParser>.Instance);
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void ParseFile_SkipsBlankAndMalformedLines()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
            "",
            "not-json",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:00Z","sessionId":"s1"}""",
        ]);

        var messages = _sut.ParseFile(_tempFile).ToList();

        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public void ParseSessionSummary_ComputesDurationAndTokensByModel()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1","cwd":"/proj"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:05:00Z","sessionId":"s1","message":{"role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":0}}}""",
        ]);

        var summary = _sut.ParseSessionSummary(_tempFile);

        Assert.NotNull(summary);
        Assert.Equal("s1", summary.SessionId);
        Assert.Equal("/proj", summary.Project);
        Assert.Equal(TimeSpan.FromMinutes(5), summary.Duration);
        Assert.Equal(160, summary.TotalTokens);
        Assert.Equal(160, summary.TokensByModel["sonnet-5"]);
        Assert.Equal(2, summary.MessageCount);
    }

    [Fact]
    public void ParseSessionSummary_EmptyFile_ReturnsNull()
    {
        File.WriteAllText(_tempFile, "");

        var summary = _sut.ParseSessionSummary(_tempFile);

        Assert.Null(summary);
    }

    [Fact]
    public void ParseSessionSummary_FallsBackToFileNameWhenNoSessionId()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z"}""",
        ]);

        var summary = _sut.ParseSessionSummary(_tempFile);

        Assert.NotNull(summary);
        Assert.Equal(Path.GetFileNameWithoutExtension(_tempFile), summary.SessionId);
    }

    [Fact]
    public void ParseSessionSummary_NoTimestamps_ReturnsNull()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","sessionId":"s1"}""",
        ]);

        var summary = _sut.ParseSessionSummary(_tempFile);

        Assert.Null(summary);
    }
}