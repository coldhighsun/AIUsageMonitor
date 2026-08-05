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
            var summary = ProgressReporter.Run("Loading usage data...", p => dataService.GetPeriodSummary(from, to, p));
            SpectreRenderer.RenderPeriodSummary(summary);
            return 0;
        });
        return command;
    }
}