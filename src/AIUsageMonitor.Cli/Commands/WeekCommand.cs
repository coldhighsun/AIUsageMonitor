using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "week" command that shows a summary of usage data for the last 7 days.
/// </summary>
public static class WeekCommand
{
    /// <summary>
    /// Creates a new instance of the "week" command.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve usage data for the last 7 days.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing the last 7 days summary.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("week", "Show last 7 days summary");
        command.SetAction(_ =>
        {
            var to = DateOnly.FromDateTime(DateTime.Today);
            var from = to.AddDays(-6);
            var summary = ProgressReporter.Run("Loading usage data...", p => dataService.GetPeriodSummary(from, to, p));
            SpectreRenderer.RenderPeriodSummary(summary);
            return 0;
        });
        return command;
    }
}