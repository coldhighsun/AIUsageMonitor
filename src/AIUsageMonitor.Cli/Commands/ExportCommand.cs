using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using System.CommandLine;
using System.Text.Json;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "export" command for exporting usage data in JSON or CSV format. This command retrieves usage statistics and model distribution data from the provided <see cref="DataService"/> and outputs it to a specified file or standard output.
/// </summary>
public static class ExportCommand
{
    /// <summary>
    /// Creates a new instance of the "export" command with the specified <see cref="DataService"/>. The command supports options for specifying the output format (JSON or CSV) and the output file path. If no output file is specified, the data will be printed to standard output.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve usage statistics and model distribution data.</param>
    /// <returns>A configured <see cref="Command"/> instance for exporting usage data.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("export", "Export usage data");
        var formatOption = new Option<string>("--format") { Description = "Output format (json or csv)", DefaultValueFactory = _ => "json" };
        var outputOption = new Option<string?>("--output") { Description = "Output file path (defaults to stdout)" };
        command.Options.Add(formatOption);
        command.Options.Add(outputOption);

        command.SetAction((parseResult) =>
        {
            var format = parseResult.GetValue(formatOption) ?? "json";
            var output = parseResult.GetValue(outputOption);

            var (cache, models) = ProgressReporter.Run("Loading usage data...",
                p => (dataService.GetStatsCache(p), dataService.GetModelDistribution(p)));

            string content;
            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                content = ExportCsv(cache, models);
            }
            else
            {
                var payload = new ExportPayload(
                    cache.TotalSessions,
                    cache.TotalMessages,
                    cache.DailyActivity,
                    cache.DailyModelTokens,
                    models.Select(m => new ExportModelDistribution(m.ModelName, m.TotalTokens, m.Percentage, m.EstimatedCost)).ToList());
                content = JsonSerializer.Serialize(payload, CoreJsonContext.Default.ExportPayload);
            }

            if (output is not null)
            {
                File.WriteAllText(output, content);
                AnsiConsole.MarkupLine($"[green]Exported to {output}[/]");
            }
            else
            {
                Console.WriteLine(content);
            }

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Exports the usage data and model distribution to a CSV formatted string. The CSV includes daily activity statistics and model distribution details, with appropriate headers for each section.
    /// </summary>
    /// <param name="cache">The cached usage statistics to export.</param>
    /// <param name="models">The model distribution data to export.</param>
    /// <returns>A CSV formatted string representing the usage data and model distribution.</returns>
    private static string ExportCsv(
        StatsCache cache,
        List<ModelDistribution> models)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("date,messages,sessions,tool_calls");
        foreach (var day in cache.DailyActivity)
        {
            sb.AppendLine($"{day.Date:yyyy-MM-dd},{day.MessageCount},{day.SessionCount},{day.ToolCallCount}");
        }

        sb.AppendLine();
        sb.AppendLine("model,total_tokens,percentage,estimated_cost");
        foreach (var m in models)
        {
            sb.AppendLine($"{m.ModelName},{m.TotalTokens},{m.Percentage:F1},{m.EstimatedCost:F2}");
        }

        return sb.ToString();
    }
}