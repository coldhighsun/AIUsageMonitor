using System.Text.Json.Serialization;

namespace AIUsageMonitor.Core.Providers.Claude.Models;

/// <summary>
/// Represents the content of a message in a session transcript, including the message content, model used, role of the sender, and token usage information.
/// </summary>
public sealed class MessageContent
{
    /// <summary>
    /// Gets the raw content of the message, which may be a string or a structured JSON element.
    /// </summary>
    [JsonPropertyName("content")]
    public System.Text.Json.JsonElement? Content { get; init; }

    /// <summary>
    /// Gets the name of the model that generated the message.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>
    /// Gets the role of the message sender (e.g., "user" or "assistant").
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    /// <summary>
    /// Gets the token usage information associated with the message.
    /// </summary>
    [JsonPropertyName("usage")]
    public TokenUsage? Usage { get; init; }
}

/// <summary>
/// Represents a single message in a session transcript, containing information about the message type, timestamp, UUID, session ID, current working directory, and the message content.
/// </summary>
public sealed class SessionMessage
{
    /// <summary>
    /// Gets the current working directory recorded at the time the message was created.
    /// </summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>
    /// Gets the message content, including role, model, and token usage.
    /// </summary>
    [JsonPropertyName("message")]
    public MessageContent? Message { get; init; }

    /// <summary>
    /// Gets the identifier of the session this message belongs to.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the timestamp indicating when the message was recorded.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }

    /// <summary>
    /// Gets the type of the message (e.g., "user" or "assistant").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>
    /// Gets the unique identifier of the message.
    /// </summary>
    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }
}

/// <summary>
/// Represents token usage statistics for a message, including cache and input/output token counts.
/// </summary>
public sealed class TokenUsage
{
    /// <summary>
    /// Gets the number of tokens used to create the cache.
    /// </summary>
    [JsonPropertyName("cache_creation_input_tokens")]
    public long CacheCreationInputTokens { get; init; }

    /// <summary>
    /// Gets the number of tokens read from the cache.
    /// </summary>
    [JsonPropertyName("cache_read_input_tokens")]
    public long CacheReadInputTokens { get; init; }

    /// <summary>
    /// Gets the number of input tokens consumed by the message.
    /// </summary>
    [JsonPropertyName("input_tokens")]
    public long InputTokens { get; init; }

    /// <summary>
    /// Gets the number of output tokens produced by the message.
    /// </summary>
    [JsonPropertyName("output_tokens")]
    public long OutputTokens { get; init; }
}