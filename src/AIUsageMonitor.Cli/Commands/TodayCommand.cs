using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "today" command in the CLI application, which shows a day's usage summary (today by default).
/// </summary>
public static class TodayCommand
{
    /// <summary>
    /// Creates a new instance of the "today" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the usage summary and recent activity.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing a day's usage summary.</returns>
    public static Command Create(DataService dataService, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var command = new Command("today", "Show today's usage summary (or another day's with --date)");
        var rangeOptions = new DateRangeOptions(command, singleDay: true);
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var today = DateRangeResolver.Today(clock);
            var resolution = DateRangeResolver.Resolve(rangeOptions.Read(parseResult), today, TimeSpan.FromDays(1));
            if (resolution.Range is not { } range)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var date = range.From;
            var json = parseResult.GetValue(jsonOption);
            var summary = JsonOutput.Load(json, "Loading usage data...", p => dataService.GetDailySummary(date, p))
                ?? DailySummary.Empty(date);

            JsonOutput.Emit(json, summary, CliJsonContext.Default.DailySummary, s => Render(dataService, s, date == today, clock));
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Renders a day's summary, followed by the hourly chart when the day is today.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the recent activity.</param>
    /// <param name="summary">The day's summary.</param>
    /// <param name="isToday">Whether the summarised day is today; the hourly chart covers the time since midnight, so it only applies then.</param>
    /// <param name="clock">The clock used to measure the time since midnight.</param>
    private static void Render(DataService dataService, DailySummary summary, bool isToday, TimeProvider clock)
    {
        if (!isToday)
        {
            SpectreRenderer.RenderDailySummary(summary);
            return;
        }

        // Midnight is built in the clock's own zone; "now - now.Date" would convert it with the machine's zone instead.
        var now = clock.GetLocalNow();
        var midnight = new DateTimeOffset(now.Date, clock.LocalTimeZone.GetUtcOffset(now.Date));
        var recent = ProgressReporter.Run("Loading recent activity...",
            p => dataService.GetRecentActivity(now - midnight, p));

        AnsiConsole.Write(new Rows(
            SpectreRenderer.BuildDailySummary(summary),
            new Rule().RuleStyle("grey"),
            SpectreRenderer.BuildHourlyTokenChart(recent.HourlyTrend)));
    }
}
