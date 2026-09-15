using System.Text.Json.Serialization;

namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Represents the subset of GitHub's "get the latest release" API response used to determine
/// the latest published version.
/// </summary>
public sealed class GitHubReleaseResponse
{
    /// <summary>
    /// Gets or sets the release's git tag name, e.g. <c>v1.2.3</c>.
    /// </summary>
    [JsonPropertyName("tag_name")]
    public string? TagName
    {
        get; set;
    }
}