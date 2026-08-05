using System.CommandLine;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using Spectre.Console;

namespace AIUsageMonitor.Cli.Commands;

public static class TodayCommand
{
    public static Command Create(DataService dataService)
    {
        var command = new Command("today", "Show today's usage summary");
        var recentHoursOption = new Option<int>("--recent-hours")
        {
            Description = "Trailing window (in hours) for the recent activity block",
            DefaultValueFactory = _ => 6
        };
        command.Options.Add(recentHoursOption);

        command.SetAction(parseResult =>
        {
            var summary = ProgressReporter.Run("Loading usage data...",
                p => dataService.GetDailySummary(DateOnly.FromDateTime(DateTime.Today), p));
            if (summary is null)
            {
                AnsiConsole.MarkupLine("[yellow]No data for today.[/]");
                return 0;
            }

            var recentHours = Math.Max(1, parseResult.GetValue(recentHoursOption));
            var recent = ProgressReporter.Run("Loading recent activity...",
                p => dataService.GetRecentActivity(TimeSpan.FromHours(recentHours), p));

            AnsiConsole.Write(new Rows(
                SpectreRenderer.BuildDailySummary(summary),
                new Rule().RuleStyle("grey"),
                SpectreRenderer.BuildHourlyTokenChart(recent.HourlyTrend)));
            return 0;
        });
        return command;
    }
}
