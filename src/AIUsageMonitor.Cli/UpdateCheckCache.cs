using System.Text.Json;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Persists the outcome of the last GitHub update check under the user's local application data folder, so that
/// most invocations can answer "is there a newer release?" from disk instead of making a network request.
/// </summary>
/// <param name="filePath">The path of the JSON file the cache entry is stored in.</param>
/// <param name="timeProvider">The clock used to stamp and expire entries.</param>
internal sealed class UpdateCheckCache(string filePath, TimeProvider timeProvider)
{
    /// <summary>
    /// How long a successful check stays valid.
    /// </summary>
    public static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a failed check (e.g. offline) is remembered, so that every command does not wait on a dead network.
    /// </summary>
    public static readonly TimeSpan FailureLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the default cache file location (<c>%LOCALAPPDATA%\aimon\update-check.json</c>).
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "update-check.json");

    /// <summary>
    /// Returns the cached outcome if it was recorded for <paramref name="currentVersion"/> and has not expired.
    /// </summary>
    /// <param name="currentVersion">The version of the running application; an entry for another version is ignored.</param>
    /// <returns>The cached outcome, or <see langword="null"/> when there is no usable entry.</returns>
    public UpdateCheckOutcome? TryGet(string currentVersion)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(filePath));
            if (entry is null || entry.CurrentVersion != currentVersion)
            {
                return null;
            }

            var lifetime = entry.Succeeded ? SuccessLifetime : FailureLifetime;
            var age = timeProvider.GetUtcNow() - entry.CheckedAt;
            if (age < TimeSpan.Zero || age >= lifetime)
            {
                return null;
            }

            return new UpdateCheckOutcome(entry.IsUpdateAvailable, entry.LatestVersion, UpdateChecking.ReleaseUrl);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the outcome of a check. Failing to write is ignored: the cache is only an optimization.
    /// </summary>
    /// <param name="currentVersion">The version of the running application the check was made for.</param>
    /// <param name="outcome">The outcome to remember.</param>
    /// <param name="succeeded">Whether the check reached GitHub; failed checks expire sooner.</param>
    public void Save(string currentVersion, UpdateCheckOutcome outcome, bool succeeded)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var entry = new CacheEntry(
                timeProvider.GetUtcNow(), currentVersion, succeeded, outcome.IsUpdateAvailable, outcome.LatestVersion);

            // Written to a temporary file first so a concurrent reader never sees a half-written entry.
            var tempPath = $"{filePath}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(entry));
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only; the next invocation simply checks again.
        }
    }

    /// <summary>
    /// The persisted form of a check outcome.
    /// </summary>
    /// <param name="CheckedAt">When the check was made.</param>
    /// <param name="CurrentVersion">The running version the check was made for.</param>
    /// <param name="Succeeded">Whether the check reached GitHub.</param>
    /// <param name="IsUpdateAvailable">Whether a newer release was found.</param>
    /// <param name="LatestVersion">The latest version found, if any.</param>
    private sealed record CacheEntry(
        DateTimeOffset CheckedAt, string CurrentVersion, bool Succeeded, bool IsUpdateAvailable, string? LatestVersion);
}
