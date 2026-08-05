using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

public static class HoursCommand
{
    public static Command Create(DataService dataService)
    {
        var command = new Command("hours", "Show hourly activity distribution");
        command.SetAction(_ =>
        {
            var hours = ProgressReporter.Run("Loading usage data...", dataService.GetHourlyActivity);
            SpectreRenderer.RenderHourlyActivity(hours);
            return 0;
        });
        return command;
    }
}