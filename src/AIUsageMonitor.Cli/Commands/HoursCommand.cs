using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "hours" command, which shows token usage by hour of day.
/// </summary>
public static class HoursCommand
{
    /// <summary>
    /// Creates a new instance of the "hours" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the hourly activity.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing the hourly distribution.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("hours", "Show hourly activity distribution");
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            var hours = JsonOutput.Load(json, "Loading usage data...", dataService.GetHourlyActivity);
            JsonOutput.Emit(json, hours, CliJsonContext.Default.ListHourlyActivity, SpectreRenderer.RenderHourlyActivity);
            return 0;
        });
        return command;
    }
}
