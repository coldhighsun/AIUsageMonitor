using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Parses a history file containing JSON lines into a collection of <see cref="HistoryEntry"/> objects.
/// </summary>
public sealed class HistoryParser
{
    /// <summary>
    /// Parses the specified history file and returns a collection of <see cref="HistoryEntry"/> objects.
    /// </summary>
    /// <param name="filePath">The path to the history file to parse.</param>
    /// <returns>A collection of <see cref="HistoryEntry"/> objects.</returns>
    public IEnumerable<HistoryEntry> Parse(string filePath)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            HistoryEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, CoreJsonContext.Default.HistoryEntry);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null)
            {
                yield return entry;
            }
        }
    }
}