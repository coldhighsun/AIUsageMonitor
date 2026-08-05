using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the command to show the last 30 days summary of AI usage.
/// </summary>
public static class MonthCommand
{
    /// <summary>
    /// Creates a new instance of the MonthCommand.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve AI usage data.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing the last 30 days summary of AI usage.</returns>
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