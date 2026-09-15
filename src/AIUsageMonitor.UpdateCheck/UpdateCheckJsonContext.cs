using System.Text.Json.Serialization;

namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Represents the JSON serialization context used to deserialize GitHub release API responses
/// without reflection.
/// </summary>
[JsonSerializable(typeof(GitHubReleaseResponse))]
public sealed partial class UpdateCheckJsonContext : JsonSerializerContext;