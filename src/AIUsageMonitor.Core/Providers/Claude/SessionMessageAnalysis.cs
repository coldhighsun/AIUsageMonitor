using System.Text.Json;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

internal static class SessionMessageAnalysis
{
    public static int CountToolCalls(SessionMessage msg)
    {
        if (msg.Message?.Content is not { ValueKind: JsonValueKind.Array } content)
        {
            return 0;
        }

        var count = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "tool_use")
            {
                count++;
            }
        }

        return count;
    }
}
