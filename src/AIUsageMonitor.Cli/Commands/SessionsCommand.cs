using System.CommandLine;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;

namespace AIUsageMonitor.Cli.Commands;

public static class SessionsCommand
{
    public static Command Create(DataService dataService)
    {
        var command = new Command("sessions", "Show session statistics");
        command.SetAction(_ =>
        {
            var stats = ProgressReporter.Run("Loading usage data...", dataService.GetSessionStats);
            SpectreRenderer.RenderSessionStats(stats);
            return 0;
        });
        return command;
    }
}