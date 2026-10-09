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

    /// <summary>
    /// Verifies that an invalidation drops the cached stats so the next call computes them again.
    /// </summary>
    [Fact]
    public void InvalidateStatsCache_AfterCaching_ComputesAgainOnNextCall()
    {
        using var sut = CreateSut();
        sut.GetStatsCache();

        sut.InvalidateStatsCache();
        sut.GetStatsCache();

        Assert.Equal(2, _provider.StatsCacheCallCount);
    }

    /// <summary>
    /// Verifies that stats invalidated while being computed are still shared with the next call, so the several
    /// reads of one refresh do not each recompute them.
    /// </summary>
    [Fact]
    public async Task GetStatsCache_InvalidatedWhileComputing_ReusesTheResultForTheNextCall()
    {
        using var sut = CreateSut();

        await GetStatsCacheInvalidatedWhileComputingAsync(sut);
        sut.GetStatsCache();

        Assert.Equal(1, _provider.StatsCacheCallCount);
    }

    /// <summary>
    /// Verifies that stats invalidated while being computed do not survive a further invalidation.
    /// </summary>
    [Fact]
    public async Task GetStatsCache_InvalidatedWhileComputingAndAgain_ComputesAgain()
    {
        using var sut = CreateSut();

        await GetStatsCacheInvalidatedWhileComputingAsync(sut);
        sut.InvalidateStatsCache();
        sut.GetStatsCache();

        Assert.Equal(2, _provider.StatsCacheCallCount);
    }

    /// <summary>
    /// Starts a stats computation, invalidates the cache while the provider is still working, and lets it finish.
    /// </summary>
    /// <param name="sut">The service under test.</param>
    /// <returns>A task that completes once the computation has finished.</returns>
    private async Task GetStatsCacheInvalidatedWhileComputingAsync(DataService sut)
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _provider.OnGetStatsCache = () =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        };

        var computation = Task.Run(() => sut.GetStatsCache(), TestContext.Current.CancellationToken);
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        sut.InvalidateStatsCache();
        release.Set();
        await computation;
        _provider.OnGetStatsCache = null;
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

    /// <summary>
    /// Verifies that session usage is requested from the provider with the days and model, and filtered by project.
    /// </summary>
    [Fact]
    public void GetSessionUsage_ProjectFilter_KeepsMatchingSessionsOnly()
    {
        _provider.SessionUsage = [SessionOf("s1", "C--repos-app", "C:/repos/app"), SessionOf("s2", "C--repos-other", "C:/repos/other")];
        using var sut = CreateSut();

        var sessions = sut.GetSessionUsage(new(2026, 8, 1), new(2026, 8, 31), "opus", "repos/app");

        Assert.Equal("s1", Assert.Single(sessions).SessionId);
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), "opus"), _provider.LastSessionUsageRequest);
    }

    /// <summary>
    /// Verifies that project usage groups the sessions that pass the filter.
    /// </summary>
    [Fact]
    public void GetProjectUsage_SessionsOfTwoProjects_GroupsThem()
    {
        _provider.SessionUsage = [SessionOf("s1", "p1", null), SessionOf("s2", "p1", null), SessionOf("s3", "p2", null)];
        using var sut = CreateSut();

        var projects = sut.GetProjectUsage(new(2026, 8, 1), new(2026, 8, 31));

        Assert.Equal(2, projects.Count);
        Assert.Equal(2, projects.Single(p => p.ProjectKey == "p1").Sessions);
    }

    /// <summary>
    /// Creates a session with only the project fields set.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="key">The project folder.</param>
    /// <param name="path">The working directory, if any.</param>
    /// <returns>The session.</returns>
    private static SessionUsage SessionOf(string id, string key, string? path)
    {
        var start = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        return new SessionUsage(id, key, path, start, start.AddMinutes(1), 1, 0, 10, 10, 0, 0, 0, [], 0m);
    }

    private sealed class FakeUsageProvider : IUsageProvider
    {
        public int StatsCacheCallCount { get; private set; }
        /// <summary>
        /// Gets or sets an action run inside <see cref="GetStatsCache"/> after the call has been counted, e.g. to block it.
        /// </summary>
        public Action? OnGetStatsCache { get; set; }
        public StatsCache StatsCache { get; set; } = new();
        public List<HourlyActivity> HourlyActivity { get; set; } = [];
        public TimeSpan? LastRecentActivityWindow { get; private set; }
        public DateTimeOffset? LastSessionResetAt { get; private set; }
        public (DayOfWeek Day, TimeSpan TimeOfDay)? LastWeekAnchor { get; private set; }

        public string Name => "Fake";

        /// <summary>
        /// Gets or sets the sessions returned to callers.
        /// </summary>
        public List<SessionUsage> SessionUsage { get; set; } = [];

        /// <summary>
        /// Gets the days and model filter of the most recent session-usage request.
        /// </summary>
        public (DateOnly From, DateOnly To, string? Model)? LastSessionUsageRequest { get; private set; }


        /// <inheritdoc />
        public List<SessionUsage> GetSessionUsage(DateOnly from, DateOnly to, string? model, IProgress<int>? progress = null)
        {
            LastSessionUsageRequest = (from, to, model);
            return SessionUsage;
        }

        public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null) => HourlyActivity;

        public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
        {
            LastRecentActivityWindow = window;
            return new(window, 0, 0, 0, 0, [], 0m, []);
        }

        public StatsCache GetStatsCache(IProgress<int>? progress = null)
        {
            StatsCacheCallCount++;
            OnGetStatsCache?.Invoke();
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
