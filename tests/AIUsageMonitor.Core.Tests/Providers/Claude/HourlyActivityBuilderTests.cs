using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class HourlyActivityBuilderTests : IDisposable
{
    private readonly SessionParser _sessionParser = new(NullLogger<SessionParser>.Instance);
    private readonly HourlyActivityBuilder _sut;
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    public HourlyActivityBuilderTests()
    {
        _sut = new HourlyActivityBuilder(new SessionFileCache(_sessionParser, NullLogger<SessionFileCache>.Instance));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Build_DuplicateMessageId_CountsTokensOnce()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"assistant","timestamp":"2026-08-01T09:00:00Z","sessionId":"s1","requestId":"req1","message":{"id":"msg1","role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":0}}}""",
            """{"type":"assistant","timestamp":"2026-08-01T09:00:05Z","sessionId":"s1","requestId":"req1","message":{"id":"msg1","role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":0}}}""",
        ]);

        var result = _sut.Build([_tempFile]);

        var hour = result.Single(h => h.Hour == new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero).LocalDateTime.Hour);
        Assert.Equal(160, hour.TotalTokens);
    }

    [Fact]
    public void Build_DistinctMessages_SumsTokensAcrossHours()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"assistant","timestamp":"2026-08-01T09:00:00Z","sessionId":"s1","requestId":"req1","message":{"id":"msg1","role":"assistant","model":"sonnet-5","usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""",
            """{"type":"assistant","timestamp":"2026-08-01T10:00:00Z","sessionId":"s1","requestId":"req2","message":{"id":"msg2","role":"assistant","model":"sonnet-5","usage":{"input_tokens":20,"output_tokens":10,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""",
        ]);

        var result = _sut.Build([_tempFile]);

        var hour9 = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero).LocalDateTime.Hour;
        var hour10 = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero).LocalDateTime.Hour;
        Assert.Equal(150, result.Single(h => h.Hour == hour9).TotalTokens);
        Assert.Equal(30, result.Single(h => h.Hour == hour10).TotalTokens);
    }
}
