using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using AIUsageMonitor.WPF.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace AIUsageMonitor.WPF.Tests;

/// <summary>
/// Tests for the date-range handling and chart refresh of <see cref="DashboardViewModel"/>.
/// </summary>
public class DashboardViewModelTests : IDisposable
{
    /// <summary>
    /// The day the view model is opened on.
    /// </summary>
    private static readonly DateOnly Today = new(2026, 10, 9);

    /// <summary>
    /// A directory that stands in for the Claude data directory, so no real file watchers are created.
    /// </summary>
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    /// <summary>
    /// The provider that supplies the data behind the view model.
    /// </summary>
    private readonly FakeUsageProvider _provider = new();

    /// <summary>
    /// The clock the view model reads "today" from.
    /// </summary>
    private readonly ManualTimeProvider _clock = new(Today);

    /// <summary>
    /// The data service wrapped around <see cref="_provider"/>.
    /// </summary>
    private readonly DataService _dataService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardViewModelTests"/> class.
    /// </summary>
    public DashboardViewModelTests()
    {
        _provider.StatsCache = new StatsCache
        {
            DailyActivity = [new() { Date = Today, MessageCount = 3, SessionCount = 1, ToolCallCount = 0 }],
        };
        _provider.HourlyActivity = [new(9, 100)];
        _dataService = new(
            _provider,
            new UsageAnalyzer(new CostCalculator()),
            new SessionFileCache(new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance),
            new SessionActivityTracker(new ClaudeDataLocator(_tempDir)),
            NullLogger<DataService>.Instance);
    }

    /// <summary>
    /// The view models created by the test, stopped on cleanup so no refresh timer outlives its test.
    /// </summary>
    private readonly List<DashboardViewModel> _created = [];

    /// <summary>
    /// Stops the view models and releases the data service.
    /// </summary>
    public void Dispose()
    {
        foreach (var viewModel in _created)
        {
            viewModel.Dispose();
        }

        _dataService.Dispose();
    }

    /// <summary>
    /// Creates a view model on the test clock and remembers it for cleanup.
    /// </summary>
    /// <returns>The new view model.</returns>
    private DashboardViewModel CreateSut()
    {
        var viewModel = new DashboardViewModel(_dataService, _clock);
        _created.Add(viewModel);

        return viewModel;
    }

    /// <summary>
    /// Verifies that disposing the view model stops its periodic refresh.
    /// </summary>
    [Fact]
    public void Dispose_RefreshTimerRunning_StopsIt()
    {
        var sut = CreateSut();
        Assert.True(sut.IsRefreshTimerRunning);

        sut.Dispose();

        Assert.False(sut.IsRefreshTimerRunning);
    }

    /// <summary>
    /// Verifies that a range ending today is moved forward once the day changes, and that the data is reloaded exactly once.
    /// </summary>
    [Fact]
    public void OnTimerTick_RangeEndsOnPreviousDay_ShiftsRangeAndReloadsOnce()
    {
        var sut = CreateSut();
        var loadsBefore = _provider.HourlyCallCount;

        _clock.SetToday(Today.AddDays(1));
        sut.OnTimerTick();

        Assert.Equal(new DateTime(2026, 10, 10), sut.DateTo);
        Assert.Equal(new DateTime(2026, 9, 11), sut.DateFrom);
        Assert.Equal(loadsBefore + 1, _provider.HourlyCallCount);
    }

    /// <summary>
    /// Verifies that a range whose end the user picked is left alone when the day changes.
    /// </summary>
    [Fact]
    public void OnTimerTick_UserPickedAnotherEnd_KeepsRange()
    {
        var sut = CreateSut();
        sut.DateFrom = new DateTime(2026, 8, 1);
        sut.DateTo = new DateTime(2026, 8, 31);

        _clock.SetToday(Today.AddDays(1));
        sut.OnTimerTick();

        Assert.Equal(new DateTime(2026, 8, 1), sut.DateFrom);
        Assert.Equal(new DateTime(2026, 8, 31), sut.DateTo);
    }

    /// <summary>
    /// Verifies that the daily chart is cleared, not left showing the previous range, when the new range has no data.
    /// </summary>
    [Fact]
    public void DateRangeChanged_NoDataInNewRange_ClearsDailyChart()
    {
        var sut = CreateSut();
        Assert.NotEmpty(sut.DailyUsageSeries);

        sut.DateFrom = new DateTime(2026, 1, 1);
        sut.DateTo = new DateTime(2026, 1, 2);

        Assert.Empty(sut.DailyUsageSeries);
        Assert.Empty(sut.DailyXAxes);
    }

    /// <summary>
    /// Verifies that the hourly chart is cleared when a refresh finds no hourly data.
    /// </summary>
    [Fact]
    public void Refresh_NoHourlyData_ClearsHourlyChart()
    {
        var sut = CreateSut();
        Assert.NotEmpty(sut.HourlyActivitySeries);

        _provider.HourlyActivity = [];
        sut.RefreshCommand.Execute(null);

        Assert.Empty(sut.HourlyActivitySeries);
        Assert.Empty(sut.HourlyXAxes);
    }

    /// <summary>
    /// A clock whose current local day can be set by the test.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        /// <summary>
        /// The current UTC time reported by the clock.
        /// </summary>
        private DateTimeOffset _utcNow;

        /// <summary>
        /// Initializes a new instance of the <see cref="ManualTimeProvider"/> class at noon on the given day.
        /// </summary>
        /// <param name="today">The initial day.</param>
        public ManualTimeProvider(DateOnly today)
        {
            SetToday(today);
        }

        /// <summary>
        /// Gets the time zone used for local time, which is UTC so the local day equals the UTC day.
        /// </summary>
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        /// <summary>
        /// Moves the clock to noon on the given day.
        /// </summary>
        /// <param name="today">The new day.</param>
        public void SetToday(DateOnly today)
        {
            _utcNow = new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
        }

        /// <summary>
        /// Gets the current UTC time.
        /// </summary>
        /// <returns>The time the test set.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    /// <summary>
    /// A usage provider with canned data that counts how often the hourly activity is read.
    /// </summary>
    private sealed class FakeUsageProvider : IUsageProvider
    {
        /// <summary>
        /// Gets how many times the hourly activity has been read, i.e. how many times the view model reloaded.
        /// </summary>
        public int HourlyCallCount { get; private set; }

        /// <summary>
        /// Gets or sets the stats cache returned to callers.
        /// </summary>
        public StatsCache StatsCache { get; set; } = new();

        /// <summary>
        /// Gets or sets the hourly activity returned to callers.
        /// </summary>
        public List<HourlyActivity> HourlyActivity { get; set; } = [];

        /// <summary>
        /// Gets the display name of the provider.
        /// </summary>
        public string Name => "Fake";

        /// <inheritdoc />
        public List<SessionUsage> GetSessionUsage(DateOnly from, DateOnly to, string? model, IProgress<int>? progress = null) => [];

        /// <inheritdoc />
        public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null)
        {
            HourlyCallCount++;

            return HourlyActivity;
        }

        /// <inheritdoc />
        public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
        {
            return new(window, 0, 0, 0, 0, [], 0m, []);
        }

        /// <inheritdoc />
        public StatsCache GetStatsCache(IProgress<int>? progress = null)
        {
            return StatsCache;
        }

        /// <inheritdoc />
        public UsageWindowSummary GetCurrentSessionWindow(DateTimeOffset? sessionResetAt, IProgress<int>? progress = null)
        {
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }

        /// <inheritdoc />
        public UsageWindowSummary GetWeekWindow((DayOfWeek Day, TimeSpan TimeOfDay)? anchor, IProgress<int>? progress = null)
        {
            return new(null, null, 0, 0, [], 0m, WindowConfidence.Unknown);
        }
    }
}
