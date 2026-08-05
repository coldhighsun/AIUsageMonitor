using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Parses a stats cache file in JSON format and returns a <see cref="StatsCache"/> object.
/// </summary>
public sealed class StatsCacheParser
{
    /// <summary>
    /// Parses a stats cache file in JSON format and returns a <see cref="StatsCache"/> object.
    /// </summary>
    /// <param name="filePath">The path to the stats cache file to parse.</param>
    /// <returns>A <see cref="StatsCache"/> object representing the parsed data.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the file cannot be deserialized into a <see cref="StatsCache"/> object.</exception>
    public StatsCache Parse(string filePath)
    {
        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize(json, CoreJsonContext.Default.StatsCache)
            ?? throw new InvalidOperationException($"Failed to deserialize {filePath}");
    }
}