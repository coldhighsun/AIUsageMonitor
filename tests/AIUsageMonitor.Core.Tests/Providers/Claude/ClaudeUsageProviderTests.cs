using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class ClaudeUsageProviderTests : IDisposable
{
    private readonly string _claudeDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");
    private readonly ClaudeUsageProvider _sut;

    public ClaudeUsageProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_claudeDir, "projects"));

        var locator = new ClaudeDataLocator(_claudeDir);
        var sessionParser = new SessionParser(NullLogger<SessionParser>.Instance);
        var sessionFileCache = new SessionFileCache(sessionParser, NullLogger<SessionFileCache>.Instance);
        var costCalculator = new CostCalculator();

        _sut = new ClaudeUsageProvider(
            locator,
            new StatsCacheParser(),
            new StatsCacheBuilder(sessionFileCache),
            new RecentActivityBuilder(sessionFileCache, costCalculator),
            new HourlyActivityBuilder(sessionFileCache),
            new SessionActivityTracker(locator),
            NullLogger<ClaudeUsageProvider>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_claudeDir))
        {
            Directory.Delete(_claudeDir, recursive: true);
        }
    }

    [Fact]
    public void GetStatsCache_SessionFileNewerThanStatsCache_RebuildsFromTranscripts()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var statsCachePath = Path.Combine(_claudeDir, "stats-cache.json");
        File.WriteAllText(statsCachePath, $$"""
        {
            "version": 1,
            "lastComputedDate": "{{today:yyyy-MM-dd}}",
            "totalSessions": 1,
            "totalMessages": 1,
            "modelUsage": {
                "stale-model": { "inputTokens": 1, "outputTokens": 1 }
            }
        }
        """);
        File.SetLastWriteTimeUtc(statsCachePath, DateTime.UtcNow.AddMinutes(-30));

        var sessionFile = Path.Combine(_claudeDir, "projects", "session.jsonl");
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllLines(sessionFile,
        [
            "{\"type\":\"user\",\"timestamp\":\"" + timestamp + "\",\"sessionId\":\"s1\"}",
            "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"sessionId\":\"s1\",\"message\":{\"role\":\"assistant\",\"model\":\"fresh-model\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}",
        ]);
        File.SetLastWriteTimeUtc(sessionFile, DateTime.UtcNow);

        var cache = _sut.GetStatsCache();

        Assert.True(cache.ModelUsage.ContainsKey("fresh-model"));
        Assert.False(cache.ModelUsage.ContainsKey("stale-model"));
    }

    [Fact]
    public void GetStatsCache_StatsCacheNewerThanSessions_UsesStatsCacheAsIs()
    {
        var sessionFile = Path.Combine(_claudeDir, "projects", "session.jsonl");
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllLines(sessionFile,
        [
            "{\"type\":\"user\",\"timestamp\":\"" + timestamp + "\",\"sessionId\":\"s1\"}",
        ]);
        File.SetLastWriteTimeUtc(sessionFile, DateTime.UtcNow.AddMinutes(-30));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var statsCachePath = Path.Combine(_claudeDir, "stats-cache.json");
        File.WriteAllText(statsCachePath, $$"""
        {
            "version": 1,
            "lastComputedDate": "{{today:yyyy-MM-dd}}",
            "totalSessions": 1,
            "totalMessages": 1,
            "modelUsage": {
                "cached-model": { "inputTokens": 1, "outputTokens": 1 }
            }
        }
        """);
        File.SetLastWriteTimeUtc(statsCachePath, DateTime.UtcNow);

        var cache = _sut.GetStatsCache();

        Assert.True(cache.ModelUsage.ContainsKey("cached-model"));
    }

    [Fact]
    public void GetStatsCache_DuplicateMessageIdInTranscript_CountsTokensOnce()
    {
        var sessionFile = Path.Combine(_claudeDir, "projects", "session.jsonl");
        var t1 = DateTimeOffset.UtcNow.ToString("O");
        var t2 = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");
        File.WriteAllLines(sessionFile,
        [
            "{\"type\":\"user\",\"timestamp\":\"" + t1 + "\",\"sessionId\":\"s1\"}",
            "{\"type\":\"assistant\",\"timestamp\":\"" + t1 + "\",\"sessionId\":\"s1\",\"requestId\":\"req1\",\"message\":{\"id\":\"msg1\",\"role\":\"assistant\",\"model\":\"fresh-model\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}",
            "{\"type\":\"assistant\",\"timestamp\":\"" + t2 + "\",\"sessionId\":\"s1\",\"requestId\":\"req1\",\"message\":{\"id\":\"msg1\",\"role\":\"assistant\",\"model\":\"fresh-model\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}",
        ]);
        File.SetLastWriteTimeUtc(sessionFile, DateTime.UtcNow);

        var cache = _sut.GetStatsCache();

        Assert.Equal(150, cache.ModelUsage["fresh-model"].InputTokens + cache.ModelUsage["fresh-model"].OutputTokens);
    }
}
