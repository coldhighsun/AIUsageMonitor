using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "watch" command, which continuously refreshes a usage view at a fixed interval.
/// </summary>
public static class WatchCommand
{
    /// <summary>
    /// Creates the "watch" command with its options and action.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve usage data for the specified view.</param>
    /// <returns>A configured <see cref="Command"/> instance for continuously refreshing a usage view.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("watch", "Continuously refresh a usage view at a fixed interval");
        var viewOption = new Option<string>("--view")
        {
            Description = "View to refresh: today, week, models, sessions, hours",
            DefaultValueFactory = _ => "today"
        };
        var intervalOption = new Option<int>("--interval")
        {
            Description = "Refresh interval in seconds",
            DefaultValueFactory = _ => 2
        };
        command.Options.Add(viewOption);
        command.Options.Add(intervalOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var view = parseResult.GetValue(viewOption)!;
            var interval = Math.Max(1, parseResult.GetValue(intervalOption));

            IRenderable BuildCurrent(IProgress<int>? progress = null) => view switch
            {
                "today" => new Rows(
                    SpectreRenderer.BuildDailySummary(
                        dataService.GetDailySummary(DateOnly.FromDateTime(DateTime.Today), progress)
                            ?? DailySummary.Empty(DateOnly.FromDateTime(DateTime.Today))),
                    new Rule().RuleStyle("grey"),
                    SpectreRenderer.BuildHourlyTokenChart(dataService.GetRecentActivity(DateTimeOffset.Now - DateTimeOffset.Now.Date, progress).HourlyTrend)),
                "week" => SpectreRenderer.BuildPeriodSummary(
                    dataService.GetPeriodSummary(DateOnly.FromDateTime(DateTime.Today).AddDays(-6), DateOnly.FromDateTime(DateTime.Today), progress)),
                "models" => SpectreRenderer.BuildModelDistribution(dataService.GetModelDistribution(progress)),
                "sessions" => SpectreRenderer.BuildSessionStats(dataService.GetSessionStats(progress)),
                "hours" => SpectreRenderer.BuildHourlyActivity(dataService.GetHourlyActivity(progress)),
                _ => new Markup($"[red]Unknown view: {view}. Use today|week|models|sessions|hours.[/]")
            };

            AnsiConsole.Clear();

            var initial = ProgressReporter.Run("Loading usage data...", p => BuildCurrent(p));

            await AnsiConsole.Live(initial)
                .StartAsync(async ctx =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        ctx.UpdateTarget(BuildCurrent());
                        ctx.Refresh();
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                });

            return 0;
        });

        return command;
    }
}