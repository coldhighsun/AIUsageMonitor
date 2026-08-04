using System.CommandLine;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AIUsageMonitor.Cli.Commands;

public static class WatchCommand
{
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
        var recentHoursOption = new Option<int>("--recent-hours")
        {
            Description = "Trailing window (in hours) for the hourly token chart shown in the today view",
            DefaultValueFactory = _ => 6
        };
        command.Options.Add(viewOption);
        command.Options.Add(intervalOption);
        command.Options.Add(recentHoursOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var view = parseResult.GetValue(viewOption)!;
            var interval = Math.Max(1, parseResult.GetValue(intervalOption));
            var recentHours = Math.Max(1, parseResult.GetValue(recentHoursOption));

            IRenderable BuildCurrent() => view switch
            {
                "today" => dataService.GetDailySummary(DateOnly.FromDateTime(DateTime.Today)) is { } d
                    ? new Rows(
                        SpectreRenderer.BuildDailySummary(d),
                        new Rule().RuleStyle("grey"),
                        SpectreRenderer.BuildHourlyTokenChart(dataService.GetRecentActivity(TimeSpan.FromHours(recentHours)).HourlyTrend))
                    : new Markup("[yellow]No data for today.[/]"),
                "week" => SpectreRenderer.BuildPeriodSummary(
                    dataService.GetPeriodSummary(DateOnly.FromDateTime(DateTime.Today).AddDays(-6), DateOnly.FromDateTime(DateTime.Today))),
                "models" => SpectreRenderer.BuildModelDistribution(dataService.GetModelDistribution()),
                "sessions" => SpectreRenderer.BuildSessionStats(dataService.GetSessionStats()),
                "hours" => SpectreRenderer.BuildHourlyActivity(dataService.GetHourlyActivity()),
                _ => new Markup($"[red]Unknown view: {view}. Use today|week|models|sessions|hours.[/]")
            };

            AnsiConsole.Clear();

            await AnsiConsole.Live(BuildCurrent())
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
