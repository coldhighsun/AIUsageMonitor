using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using System.CommandLine;
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
/// The fixture shared by the tests that run the report commands end to end: a data service over a fake provider holding a few
/// days of activity, a fixed clock, and a helper that runs a command while capturing what it writes.
/// </summary>
public abstract class CommandActionTestBase : IDisposable
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
    protected DataService DataService { get; }

    /// <summary>
    /// The provider behind <see cref="DataService"/>, kept to inspect how it was used.
    /// </summary>
    protected FakeProvider Provider { get; }

    /// <summary>
    /// The clock reporting <see cref="Today"/> at noon, UTC.
    /// </summary>
    protected TimeProvider Clock { get; } = new FixedTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Creates the data service over a fake provider holding a few days of activity.
    /// </summary>
    protected CommandActionTestBase()
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
        Provider = new FakeProvider(cache);
        DataService = new DataService(
            Provider,
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
        DataService.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    /// <summary>
    /// Invokes a command while capturing what it writes to standard output and standard error.
    /// </summary>
    /// <param name="command">The command to invoke.</param>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The exit code and the captured output.</returns>
    protected static (int ExitCode, string Out, string Error) Run(Command command, params string[] args)
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
    protected sealed class FakeProvider(StatsCache cache) : IUsageProvider
    {
        /// <inheritdoc />
        public string Name => "fake";

        /// <summary>
        /// Gets or sets the sessions returned to callers.
        /// </summary>
        public List<SessionUsage> Sessions { get; set; } = [];

        /// <summary>
        /// Gets the days and model filter of the most recent session-usage request.
        /// </summary>
        public (DateOnly From, DateOnly To, string? Model)? LastSessionUsageRequest { get; private set; }

        /// <inheritdoc />
        public List<SessionUsage> GetSessionUsage(DateOnly from, DateOnly to, string? model, IProgress<int>? progress = null)
        {
            LastSessionUsageRequest = (from, to, model);
            return Sessions;
        }

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
