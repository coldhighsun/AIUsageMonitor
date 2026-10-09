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
    public void ParseFile_ContentWithToolUseBlocks_CountsOnlyTopLevelToolUseBlocks()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"assistant","timestamp":"2026-08-01T00:00:00Z","message":{"role":"assistant","content":[{"type":"text","text":"hi"},{"type":"tool_use","id":"a","name":"Read","input":{"type":"tool_use","nested":[{"type":"tool_use"}]}},{"id":"b","type":"tool_use"},"stray",42,{"type":"tool_result","content":"x"}]}}""",
        ]);

        var messages = _sut.ParseFile(_tempFile).ToList();

        Assert.Equal(2, messages[0].Message!.ToolUseCount);
    }

    [Fact]
    public void ParseFile_StringOrMissingContent_ToolUseCountIsZero()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","message":{"role":"user","content":"just text"}}""",
            """{"type":"user","timestamp":"2026-08-01T00:00:01Z","message":{"role":"user"}}""",
            """{"type":"user","timestamp":"2026-08-01T00:00:02Z","message":{"role":"user","content":{"type":"tool_use"}}}""",
        ]);

        var messages = _sut.ParseFile(_tempFile).ToList();

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(0, m.Message!.ToolUseCount));
    }

    [Fact]
    public void ParseFile_IsoTimestamp_IsParsedOnceIntoDateTimeOffset()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T10:30:00.123+08:00"}""",
            """{"type":"user","timestamp":"2026-08-01T02:30:00Z"}""",
        ]);

        var messages = _sut.ParseFile(_tempFile).ToList();

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 30, 0, 123, TimeSpan.FromHours(8)), messages[0].Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 2, 30, 0, TimeSpan.Zero), messages[1].Timestamp);
    }

    [Fact]
    public void ParseFile_MissingOrUnparsableTimestamp_YieldsNullTimestampButKeepsRow()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","sessionId":"s1"}""",
            """{"type":"user","timestamp":"not a date","sessionId":"s2"}""",
            """{"type":"user","timestamp":12345,"sessionId":"s3"}""",
        ]);

        var messages = _sut.ParseFile(_tempFile).ToList();

        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Null(m.Timestamp));
    }
}