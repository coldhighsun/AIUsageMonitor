using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "today" command in the CLI application, which shows today's usage summary.
/// </summary>
public static class TodayCommand
{
    /// <summary>
    /// Creates a new instance of the "today" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve today's usage summary and recent activity.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing today's usage summary.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("today", "Show today's usage summary");

        command.SetAction(_ =>
        {
            var summary = ProgressReporter.Run("Loading usage data...",
                p => dataService.GetDailySummary(DateOnly.FromDateTime(DateTime.Today), p))
                ?? DailySummary.Empty(DateOnly.FromDateTime(DateTime.Today));

            var recent = ProgressReporter.Run("Loading recent activity...",
                p => dataService.GetRecentActivity(DateTimeOffset.Now - DateTimeOffset.Now.Date, p));

            AnsiConsole.Write(new Rows(
                SpectreRenderer.BuildDailySummary(summary),
                new Rule().RuleStyle("grey"),
                SpectreRenderer.BuildHourlyTokenChart(recent.HourlyTrend)));
            return 0;
        });
        return command;
    }
}