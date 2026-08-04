using System.CommandLine;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;

namespace AIUsageMonitor.Cli.Commands;

public static class ModelsCommand
{
    public static Command Create(DataService dataService)
    {
        var command = new Command("models", "Show model usage distribution");
        command.SetAction(_ =>
        {
            var models = dataService.GetModelDistribution();
            SpectreRenderer.RenderModelDistribution(models);
            return 0;
        });
        return command;
    }
}