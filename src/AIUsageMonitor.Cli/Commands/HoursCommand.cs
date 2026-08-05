using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "hours" command, which shows the hourly activity distribution of AI usage. This command retrieves hourly activity data from the provided <see cref="DataService"/> and renders it using the <see cref="SpectreRenderer"/>.
/// </summary>
public static class HoursCommand
{
    /// <summary>
    /// Creates a new instance of the "hours" command with the specified <see cref="DataService"/>. The command retrieves hourly activity data and renders it using the <see cref="SpectreRenderer"/>.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve hourly activity data.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing hourly activity distribution.</returns>
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