using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the command for displaying model usage distribution in the AI Usage Monitor CLI application.
/// </summary>
public static class ModelsCommand
{
    /// <summary>
    /// Creates a new instance of the "models" command, which shows the model usage distribution.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve model usage data.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing model usage distribution.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("models", "Show model usage distribution");
        command.SetAction(_ =>
        {
            var models = ProgressReporter.Run("Loading usage data...", dataService.GetModelDistribution);
            SpectreRenderer.RenderModelDistribution(models);
            return 0;
        });
        return command;
    }
}