using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the command for displaying session statistics in the AI Usage Monitor CLI application.
/// </summary>
public static class SessionsCommand
{
    /// <summary>
    /// Creates a new instance of the "sessions" command, which displays session statistics.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve session statistics.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing session statistics.</returns>
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