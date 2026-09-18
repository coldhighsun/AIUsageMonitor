using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Services;

public class DataServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");
    private readonly FakeUsageProvider _provider = new();

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private DataService CreateSut() => new(
        _provider,
        new UsageAnalyzer(new CostCalculator()),
        new SessionFileCache(new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance),
        new SessionActivityTracker(new ClaudeDataLocator(_tempDir)),
        NullLogger<DataService>.Instance);

    [Fact]
    public void GetStatsCache_CachesResultAcrossCalls()
    {
        using var sut = CreateSut();

        var first = sut.GetStatsCache();
        var second = sut.GetStatsCache();

        Assert.Same(first, second);
        Assert.Equal(1, _provider.StatsCacheCallCount);
    }

    [Fact]
    public void GetDailySummary_UsesCachedStatsCache()
    {
        _provider.StatsCache = new StatsCache
        {
            DailyActivity = [new() { Date = new(2026, 8, 1), MessageCount = 3, SessionCount = 1, ToolCallCount = 0 }],
        };
        using var sut = CreateSut();

        var summary = sut.GetDailySummary(new(2026, 8, 1));

        Assert.NotNull(summary);
        Assert.Equal(3, summary.Messages);
        Assert.Equal(1, _provider.StatsCacheCallCount);
    }

    [Fact]
    public void GetDailySummary_NoActivityForDate_ReturnsNull()
    {
        using var sut = CreateSut();

        var summary = sut.GetDailySummary(new(2026, 8, 1));

        Assert.Null(summary);
    }

    [Fact]
    public void GetHourlyActivity_DelegatesToProvider()
    {
        _provider.HourlyActivity = [new(9, 100)];
        using var sut = CreateSut();

        var result = sut.GetHourlyActivity();

        Assert.Single(result);
        Assert.Equal(9, result[0].Hour);
    }

    [Fact]
    public void GetRecentActivity_DelegatesToProviderWithWindow()
    {
        using var sut = CreateSut();

        sut.GetRecentActivity(TimeSpan.FromHours(5));

        Assert.Equal(TimeSpan.FromHours(5), _provider.LastRecentActivityWindow);
    }

    [Fact]
    public void GetCurrentSessionWindow_DelegatesToProvider()
    {
        var resetAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        using var sut = CreateSut();

        sut.GetCurrentSessionWindow(resetAt);

        Assert.Equal(resetAt, _provider.LastSessionResetAt);
    }

    [Fact]
    public void GetWeekWindow_DelegatesToProvider()
    {
        var anchor = (DayOfWeek.Monday, TimeSpan.FromHours(9));
        using var sut = CreateSut();

        sut.GetWeekWindow(anchor);

        Assert.Equal(anchor, _provider.LastWeekAnchor);
    }

    [Fact]
    public void GetSessionStats_UsesCachedStatsCache()
    {
        using var sut = CreateSut();

        sut.GetSessionStats();
        sut.GetSessionStats();

        Assert.Equal(1, _provider.StatsCacheCallCount);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenNoFileWatchersWereCreated()
    {
        var sut = CreateSut();

        var exception = Record.Exception(sut.Dispose);

        Assert.Null(exception);
    }

    private sealed class FakeUsageProvider : IUsageProvider
    {
        public int StatsCacheCallCount { get; private set; }
        public StatsCache StatsCache { get; set; } = new();
        public List<HourlyActivity> HourlyActivity { get; set; } = [];
        public TimeSpan? LastRecentActivityWindow { get; private set; }
        public DateTimeOffset? LastSessionResetAt { get; private set; }
        public (DayOfWeek Day, TimeSpan TimeOfDay)? LastWeekAnchor { get; private set; }

        public string Name => "Fake";

        public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null) => HourlyActivity;

        public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
        {
            LastRecentActivityWindow = window;
            return new(window, 0, 0, 0, 0, [], 0m, []);
        }

        public StatsCache GetStatsCache(IProgress<int>? progress = null)
        {
            StatsCacheCallCount++;
            return StatsCache;
        }

        public UsageWindowSummary GetCurrentSessionWindow(DateTimeOffset? sessionResetAt, IProgress<int>? progress = null)
        {
            LastSessionResetAt = sessionResetAt;
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }

        public UsageWindowSummary GetWeekWindow((DayOfWeek Day, TimeSpan TimeOfDay)? anchor, IProgress<int>? progress = null)
        {
            LastWeekAnchor = anchor;
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }
    }
}
