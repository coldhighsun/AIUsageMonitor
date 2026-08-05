using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// Represents a single entry in the usage history, containing information about the display name, timestamp, associated project, and session ID.
/// </summary>
public sealed class HistoryEntry
{
    /// <summary>
    /// Gets the display name associated with this history entry.
    /// </summary>
    [JsonPropertyName("display")]
    public string Display { get; init; } = "";

    /// <summary>
    /// Gets the project associated with this history entry, if any.
    /// </summary>
    [JsonPropertyName("project")]
    public string? Project { get; init; }

    /// <summary>
    /// Gets the session ID associated with this history entry, if any.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the timestamp of this history entry, represented as a long integer (typically in milliseconds since the Unix epoch).
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }
}