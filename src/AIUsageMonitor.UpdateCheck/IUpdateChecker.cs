namespace AIUsageMonitor.UpdateCheck;

/// <summary>
/// Checks GitHub for a newer published release than the one currently running.
/// </summary>
public interface IUpdateChecker
{
    /// <summary>
    /// Checks whether a newer release than the current one is available on GitHub.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>
    /// The check result. Any network, timeout, or parsing failure is treated as "no update
    /// available" rather than propagated, since this check must never break the command it runs
    /// alongside.
    /// </returns>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the outcome of an <see cref="IUpdateChecker"/> check.
/// </summary>
/// <param name="IsUpdateAvailable">Whether a newer release than the current one was found.</param>
/// <param name="LatestVersion">The latest published version, if it could be determined.</param>
/// <param name="ReleaseUrl">The page a user should visit to download the latest release.</param>
public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string ReleaseUrl);