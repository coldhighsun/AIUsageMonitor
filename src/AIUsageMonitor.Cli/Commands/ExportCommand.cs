using System.CommandLine;
using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;

namespace AIUsageMonitor.Cli.Commands;

public static class ExportCommand
{
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

            var cache = dataService.GetStatsCache();
            var models = dataService.GetModelDistribution();

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
