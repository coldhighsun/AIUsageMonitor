using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "models" command, which shows the token usage distribution across models.
/// </summary>
public static class ModelsCommand
{
    /// <summary>
    /// Creates a new instance of the "models" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the model distribution.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing the model distribution.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("models", "Show model usage distribution");
        var modelOption = new Option<string?>("--model")
        {
            Description = "Only show models whose name contains this text (case-insensitive); percentages stay relative to all models"
        };
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(modelOption);
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            var filter = parseResult.GetValue(modelOption);
            var models = FilterByModel(JsonOutput.Load(json, "Loading usage data...", dataService.GetModelDistribution), filter);

            if (!json && models.Count == 0 && !string.IsNullOrWhiteSpace(filter))
            {
                Console.Error.WriteLine($"No model matches '{filter.Trim()}'.");
                return 0;
            }

            JsonOutput.Emit(json, models, CliJsonContext.Default.ListModelDistribution, SpectreRenderer.RenderModelDistribution);
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Keeps only the models whose name contains the given text.
    /// </summary>
    /// <param name="models">The model distribution to filter.</param>
    /// <param name="filter">The case-insensitive text to look for, or <see langword="null"/> or empty to keep every model.</param>
    /// <returns>The matching models, in their original order.</returns>
    internal static List<ModelDistribution> FilterByModel(List<ModelDistribution> models, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return models;
        }

        var needle = filter.Trim();
        return models.Where(m => m.ModelName.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
