using AIUsageMonitor.Core.Providers.Claude.Models;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Provides utility methods for analyzing session messages, such as counting the number of tool calls within a message.
/// </summary>
internal static class SessionMessageAnalysis
{
    /// <summary>
    /// Counts the number of tool calls in a given session message. A tool call is identified by a block in the message content that has a "type" property with the value "tool_use".
    /// </summary>
    /// <param name="msg">The session message to analyze.</param>
    /// <returns>The number of tool calls in the session message.</returns>
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