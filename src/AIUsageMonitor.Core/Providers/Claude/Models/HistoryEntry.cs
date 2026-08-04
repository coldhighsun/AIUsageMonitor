using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

public sealed class HistoryEntry
{
    [JsonPropertyName("display")]
    public string Display { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}
