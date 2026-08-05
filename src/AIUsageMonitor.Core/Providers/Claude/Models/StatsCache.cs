using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

public sealed class DailyActivity
{
    [JsonPropertyName("date")]
    [JsonConverter(typeof(DateOnlyJsonConverter))]
    public DateOnly Date { get; init; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; init; }

    [JsonPropertyName("sessionCount")]
    public int SessionCount { get; init; }

    [JsonPropertyName("toolCallCount")]
    public int ToolCallCount { get; init; }
}

public sealed class DailyModelTokens
{
    [JsonPropertyName("date")]
    [JsonConverter(typeof(DateOnlyJsonConverter))]
    public DateOnly Date { get; init; }

    [JsonPropertyName("tokensByModel")]
    public Dictionary<string, long> TokensByModel { get; init; } = [];
}

public sealed class ModelUsageEntry
{
    [JsonPropertyName("cacheCreationInputTokens")]
    public long CacheCreationInputTokens { get; init; }

    [JsonPropertyName("cacheReadInputTokens")]
    public long CacheReadInputTokens { get; init; }

    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; init; }

    [JsonPropertyName("costUSD")]
    public decimal CostUSD { get; init; }

    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("webSearchRequests")]
    public int WebSearchRequests { get; init; }
}

public sealed class StatsCache
{
    [JsonPropertyName("dailyActivity")]
    public List<DailyActivity> DailyActivity { get; init; } = [];

    [JsonPropertyName("dailyModelTokens")]
    public List<DailyModelTokens> DailyModelTokens { get; init; } = [];

    [JsonPropertyName("firstSessionDate")]
    public DateTimeOffset? FirstSessionDate { get; init; }

    [JsonPropertyName("hourCounts")]
    public Dictionary<string, int> HourCounts { get; init; } = [];

    [JsonPropertyName("lastComputedDate")]
    [JsonConverter(typeof(DateOnlyJsonConverter))]
    public DateOnly LastComputedDate { get; init; }

    [JsonPropertyName("longestSession")]
    public LongestSessionInfo? LongestSession { get; init; }

    [JsonPropertyName("modelUsage")]
    public Dictionary<string, ModelUsageEntry> ModelUsage { get; init; } = [];

    [JsonPropertyName("totalMessages")]
    public int TotalMessages { get; init; }

    [JsonPropertyName("totalSessions")]
    public int TotalSessions { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }
}

public sealed class LongestSessionInfo
{
    [JsonPropertyName("duration")]
    public long Duration { get; init; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; init; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = "";
}