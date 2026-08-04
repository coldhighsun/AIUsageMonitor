using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

public sealed class HistoryParser
{
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
