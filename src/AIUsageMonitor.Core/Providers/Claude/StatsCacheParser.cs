using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

public sealed class StatsCacheParser
{
    public StatsCache Parse(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize(json, CoreJsonContext.Default.StatsCache)
            ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");
    }
}
