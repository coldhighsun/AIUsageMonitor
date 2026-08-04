using System.CommandLine;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;

namespace AIUsageMonitor.Cli.Commands;

public static class MonthCommand
{
    public static Command Create(DataService dataService)
    {
        var command = new Command("month", "Show last 30 days summary");
        command.SetAction(_ =>
        {
            var to = DateOnly.FromDateTime(DateTime.Today);
            var from = to.AddDays(-29);
            var summary = dataService.GetPeriodSummary(from, to);
            SpectreRenderer.RenderPeriodSummary(summary);
            return 0;
        });
        return command;
    }
}