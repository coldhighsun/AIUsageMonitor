using AIUsageMonitor.Core.Providers.Claude;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class StatsCacheParserTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"stats-{Guid.NewGuid()}.json");
    private readonly StatsCacheParser _sut = new();

    [Fact]
    public void Parse_DeserializesFullDocument()
    {
        File.WriteAllText(_tempFile, """
        {
            "version": 1,
            "lastComputedDate": "2026-08-01",
            "totalSessions": 3,
            "totalMessages": 42,
            "dailyActivity": [
                { "date": "2026-08-01", "messageCount": 10, "sessionCount": 2, "toolCallCount": 5 }
            ],
            "modelUsage": {
                "sonnet-5": { "inputTokens": 100, "outputTokens": 50 }
            },
            "hourCounts": { "9": 3 }
        }
        """);

        var cache = _sut.Parse(_tempFile);

        Assert.Equal(1, cache.Version);
        Assert.Equal(3, cache.TotalSessions);
        Assert.Single(cache.DailyActivity);
        Assert.Equal(100, cache.ModelUsage["sonnet-5"].InputTokens);
        Assert.Equal(3, cache.HourCounts["9"]);
    }

    [Fact]
    public void Parse_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            _sut.Parse(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.json")));
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
