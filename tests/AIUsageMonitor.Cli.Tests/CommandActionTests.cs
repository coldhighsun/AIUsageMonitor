using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Defines the collection that keeps console-capturing tests from running in parallel with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    /// <summary>
    /// The name of the collection.
    /// </summary>
    public const string Name = "Console";
}

/// <summary>
/// Runs the report commands end to end against a fake provider and checks what they print and return.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CommandActionTests : IDisposable
{
    /// <summary>
    /// The date the fake clock reports as today.
    /// </summary>
    private static readonly DateOnly Today = new(2026, 10, 9);

    /// <summary>
    /// The directory that stands in for the Claude data directory.
    /// </summary>
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"aimon-tests-{Guid.NewGuid():N}");

    /// <summary>
    /// The data service under test.
    /// </summary>
    private readonly DataService _dataService;

    /// <summary>
    /// The provider behind <see cref="_dataService"/>, kept to inspect how it was used.
    /// </summary>
    private readonly FakeProvider _provider;

    /// <summary>
    /// The clock reporting <see cref="Today"/> at noon, UTC.
    /// </summary>
    private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Creates the data service over a fake provider holding a few days of activity.
    /// </summary>
    public CommandActionTests()
    {
        Directory.CreateDirectory(_dataDir);
        var cache = new StatsCache
        {
            TotalSessions = 4,
            TotalMessages = 30,
            DailyActivity =
            [
                new() { Date = Today, MessageCount = 10, SessionCount = 2, ToolCallCount = 5 },
                new() { Date = Today.AddDays(-1), MessageCount = 20, SessionCount = 2, ToolCallCount = 7 },
            ],
            DailyModelTokens =
            [
                new() { Date = Today, TokensByModel = new() { ["claude-opus-5-5"] = 1000 } },
                new() { Date = Today.AddDays(-1), TokensByModel = new() { ["claude-sonnet-5-5"] = 3000 } },
            ],
            ModelUsage = new()
            {
                ["claude-opus-5-5"] = new() { InputTokens = 1000 },
                ["claude-sonnet-5-5"] = new() { InputTokens = 3000 },
            },
        };

        var locator = new ClaudeDataLocator(_dataDir);
        _provider = new FakeProvider(cache);
        _dataService = new DataService(
            _provider,
            new UsageAnalyzer(new CostCalculator()),
            new SessionFileCache(new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance),
            new SessionActivityTracker(locator),
            NullLogger<DataService>.Instance);
    }

    /// <summary>
    /// Releases the data service and removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        _dataService.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    /// <summary>
    /// Verifies that today --json prints the summary for the clock's date and nothing else.
    /// </summary>
    [Fact]
    public void Create_TodayJson_PrintsSummaryForClockDate()
    {
        var result = Run(TodayCommand.Create(_dataService, _clock), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-10-09", document.RootElement.GetProperty("date").GetString());
        Assert.Equal(10, document.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal("", result.Error);
    }

    /// <summary>
    /// Verifies that --date picks another day, and that a day without activity yields an empty summary.
    /// </summary>
    [Fact]
    public void Create_TodayJsonWithDate_PrintsThatDayOrAnEmptySummary()
    {
        var yesterday = Run(TodayCommand.Create(_dataService, _clock), "--date", "2026-10-08", "--json");
        var quiet = Run(TodayCommand.Create(_dataService, _clock), "--date", "2026-01-01", "--json");

        using var yesterdayDocument = JsonDocument.Parse(yesterday.Out);
        using var quietDocument = JsonDocument.Parse(quiet.Out);
        Assert.Equal(20, yesterdayDocument.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal(0, quietDocument.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal("2026-01-01", quietDocument.RootElement.GetProperty("date").GetString());
    }

    /// <summary>
    /// Verifies that an invalid date is reported on stderr with exit code 1 and nothing on stdout.
    /// </summary>
    [Fact]
    public void Create_TodayInvalidDate_WritesErrorToStderrAndReturnsOne()
    {
        var result = Run(TodayCommand.Create(_dataService, _clock), "--date", "2026-13-01", "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--date", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that week --last counts back from the clock's date.
    /// </summary>
    [Fact]
    public void Create_WeekJsonWithLast_CoversTheRequestedDays()
    {
        var result = Run(WeekCommand.Create(_dataService, _clock), "--last", "2", "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-10-08", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-10-09", document.RootElement.GetProperty("to").GetString());
        Assert.Equal(30, document.RootElement.GetProperty("totalMessages").GetInt32());
    }

    /// <summary>
    /// Verifies that the default ranges of week and month are 7 and 30 days.
    /// </summary>
    [Fact]
    public void Create_WeekAndMonthJsonWithoutRange_UseTheirDefaultLength()
    {
        var week = Run(WeekCommand.Create(_dataService, _clock), "--json");
        var month = Run(MonthCommand.Create(_dataService, _clock), "--json");

        using var weekDocument = JsonDocument.Parse(week.Out);
        using var monthDocument = JsonDocument.Parse(month.Out);
        Assert.Equal("2026-10-03", weekDocument.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-09-10", monthDocument.RootElement.GetProperty("from").GetString());
    }

    /// <summary>
    /// Verifies that month --month covers the whole calendar month.
    /// </summary>
    [Fact]
    public void Create_MonthJsonWithMonth_CoversTheCalendarMonth()
    {
        var result = Run(MonthCommand.Create(_dataService, _clock), "--month", "2026-09", "--json");

        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-09-01", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-09-30", document.RootElement.GetProperty("to").GetString());
    }

    /// <summary>
    /// Verifies that absurd ranges are rejected with exit code 1 rather than crashing.
    /// </summary>
    [Theory]
    [InlineData("week", "--last 2147483647", "at most")]
    [InlineData("week", "--last 0", "at least 1")]
    [InlineData("month", "--month 9999-12", "between 2000 and 2999")]
    [InlineData("week", "--from 2026-10-05 --to 2026-10-01", "must not be after")]
    public void Create_PeriodCommandsInvalidRange_ReturnOneWithMessage(string commandName, string options, string expectedError)
    {
        var command = commandName == "week" ? WeekCommand.Create(_dataService, _clock) : MonthCommand.Create(_dataService, _clock);

        var result = Run(command, [.. options.Split(' '), "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that models --json honors --model and returns an empty array when nothing matches.
    /// </summary>
    [Fact]
    public void Create_ModelsJson_FiltersByModelName()
    {
        var all = Run(ModelsCommand.Create(_dataService), "--json");
        var sonnet = Run(ModelsCommand.Create(_dataService), "--model", "SONNET", "--json");
        var none = Run(ModelsCommand.Create(_dataService), "--model", "gpt", "--json");

        using var allDocument = JsonDocument.Parse(all.Out);
        using var sonnetDocument = JsonDocument.Parse(sonnet.Out);
        using var noneDocument = JsonDocument.Parse(none.Out);
        Assert.Equal(2, allDocument.RootElement.GetArrayLength());
        Assert.Equal("claude-sonnet-5-5", Assert.Single(sonnetDocument.RootElement.EnumerateArray()).GetProperty("modelName").GetString());
        Assert.Equal(0, noneDocument.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Verifies that a filter matching nothing prints a hint on stderr instead of an empty table.
    /// </summary>
    [Fact]
    public void Create_ModelsFilterMatchesNothing_WritesHintToStderr()
    {
        var result = Run(ModelsCommand.Create(_dataService), "--model", "gpt");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("No model matches 'gpt'", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that sessions --json reports durations in seconds.
    /// </summary>
    [Fact]
    public void Create_SessionsJson_PrintsSessionStatsInSeconds()
    {
        var result = Run(SessionsCommand.Create(_dataService), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(4, document.RootElement.GetProperty("total").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("longestDurationSeconds", out _));
    }

    /// <summary>
    /// Verifies that hours --json prints the provider's hourly activity.
    /// </summary>
    [Fact]
    public void Create_HoursJson_PrintsHourlyActivity()
    {
        var result = Run(HoursCommand.Create(_dataService), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(14, Assert.Single(document.RootElement.EnumerateArray()).GetProperty("hour").GetInt32());
    }

    /// <summary>
    /// Verifies that today renders the day's table and the hourly chart, which needs the recent activity.
    /// </summary>
    [Fact]
    public void Create_TodayTable_RendersSummaryAndLoadsRecentActivity()
    {
        var result = Run(TodayCommand.Create(_dataService, _clock));

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("2026-10-09 Summary", result.Out);
        Assert.Contains("Tokens by Hour", result.Out);
        Assert.Contains("09:00", result.Out);
        Assert.Equal(1, _provider.RecentActivityCalls);
        Assert.Equal(TimeSpan.FromHours(12), _provider.LastRecentActivityWindow);
    }

    /// <summary>
    /// Verifies that another day renders its table without the since-midnight chart or loading recent activity.
    /// </summary>
    [Fact]
    public void Create_TodayTableWithOtherDate_RendersSummaryWithoutRecentActivity()
    {
        var result = Run(TodayCommand.Create(_dataService, _clock), "--date", "2026-10-08");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("2026-10-08 Summary", result.Out);
        Assert.Equal(0, _provider.RecentActivityCalls);
    }

    /// <summary>
    /// Verifies that the other report commands render a table when --json is not given.
    /// </summary>
    [Fact]
    public void Create_ReportCommandsTable_RenderFormattedOutput()
    {
        var week = Run(WeekCommand.Create(_dataService, _clock), "--last", "2");
        var models = Run(ModelsCommand.Create(_dataService));
        var hours = Run(HoursCommand.Create(_dataService));
        var sessions = Run(SessionsCommand.Create(_dataService));

        Assert.Contains("2026-10-08 ~ 2026-10-09", week.Out);
        Assert.Contains("Input", models.Out);
        Assert.Contains("14", hours.Out);
        Assert.Contains("Total Sessions", sessions.Out);
    }

    /// <summary>
    /// Invokes a command while capturing what it writes to standard output and standard error.
    /// </summary>
    /// <param name="command">The command to invoke.</param>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code and the captured output.</returns>
    private static (int ExitCode, string Out, string Error) Run(Command command, params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalConsole = AnsiConsole.Console;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(stdout),
            });
            var exitCode = command.Parse(args).Invoke();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            AnsiConsole.Console = originalConsole;
        }
    }

    /// <summary>
    /// A clock that always reports the same instant, in UTC.
    /// </summary>
    /// <param name="now">The instant to report.</param>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>
    /// A provider that serves a fixed stats cache, one hour of hourly activity and a single recent-activity bucket.
    /// </summary>
    /// <param name="cache">The stats cache to serve.</param>
    private sealed class FakeProvider(StatsCache cache) : IUsageProvider
    {
        /// <inheritdoc />
        public string Name => "fake";

        /// <inheritdoc />
        public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null) => [new(14, 500)];

        /// <summary>
        /// Gets how many times recent activity was requested.
        /// </summary>
        public int RecentActivityCalls { get; private set; }

        /// <summary>
        /// Gets the window of the most recent recent-activity request.
        /// </summary>
        public TimeSpan? LastRecentActivityWindow { get; private set; }

        /// <inheritdoc />
        public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
        {
            RecentActivityCalls++;
            LastRecentActivityWindow = window;
            return new RecentActivitySummary(
                window, 3, 1, 0, 1500, [], 0m, [new HourBucket(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), 3, 1500)]);
        }

        /// <inheritdoc />
        public StatsCache GetStatsCache(IProgress<int>? progress = null) => cache;

        /// <inheritdoc />
        public UsageWindowSummary GetCurrentSessionWindow(DateTimeOffset? sessionResetAt, IProgress<int>? progress = null) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public UsageWindowSummary GetWeekWindow((DayOfWeek Day, TimeSpan TimeOfDay)? anchor, IProgress<int>? progress = null) =>
            throw new NotSupportedException();
    }
}
