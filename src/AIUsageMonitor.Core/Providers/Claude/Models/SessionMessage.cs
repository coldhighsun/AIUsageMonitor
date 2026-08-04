using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

public sealed class SessionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("message")]
    public MessageContent? Message { get; init; }
}

public sealed class MessageContent
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; init; }

    [JsonPropertyName("content")]
    public System.Text.Json.JsonElement? Content { get; init; }
}

public sealed class TokenUsage
{
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }

    [JsonPropertyName("cache_read_input_tokens")]
    public long CacheReadInputTokens { get; init; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public long CacheCreationInputTokens { get; init; }
}
