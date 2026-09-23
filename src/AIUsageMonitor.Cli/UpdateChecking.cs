using System.Reflection;
using GitHubReleaseUpdater;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Checks GitHub for a newer published release than the one currently running, via the
/// <c>GitHubReleaseUpdater</c> package.
/// </summary>
public static class UpdateChecking
{
    /// <summary>
    /// The owner of the GitHub repository to check for releases.
    /// </summary>
    private const string Owner = "coldhighsun";

    /// <summary>
    /// The name of the GitHub repository to check for releases.
    /// </summary>
    private const string Repo = "AIUsageMonitor";

    /// <summary>
    /// The GitHub page users should visit to download the latest release.
    /// </summary>
    public const string ReleaseUrl = $"https://github.com/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// Checks whether a newer release than the current one is available on GitHub.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <param name="logger">An optional logger used to record failed update checks.</param>
    /// <returns>
    /// The check outcome. Any network, timeout, or parsing failure is treated as "no update
    /// available" rather than propagated, since this check must never break the command it runs
    /// alongside.
    /// </returns>
    public static async Task<UpdateCheckOutcome> CheckForUpdateAsync(
        CancellationToken cancellationToken = default, ILogger? logger = null)
    {
        var noUpdate = new UpdateCheckOutcome(false, null, ReleaseUrl);

        var currentVersion = GetCurrentVersionDisplayString();
        if (currentVersion is null)
        {
            return noUpdate;
        }

        try
        {
            using var updater = new ReleaseUpdater(new UpdaterOptions
            {
                Owner = Owner,
                Repo = Repo,
                CurrentVersion = currentVersion,
                Timeout = TimeSpan.FromSeconds(5),
            });

            var check = await updater.CheckForUpdateAsync(cancellationToken: cancellationToken);
            if (!check.Success)
            {
                logger?.LogWarning("Update check failed: {Error}", check.Error);
                return noUpdate;
            }

            return check.IsUpdateAvailable
                ? new UpdateCheckOutcome(true, check.Update!.Version.ToString(), ReleaseUrl)
                : noUpdate;
        }
        catch (OperationCanceledException ex)
        {
            // GitHubReleaseUpdater reports every other failure (network, timeout, parsing) via
            // UpdateCheckResult.Success/Error instead of throwing; cancellation is the one
            // exception it does let through, and this check must never break the command it runs
            // alongside, so it is swallowed here too.
            logger?.LogWarning(ex, "Update check was cancelled");
            return noUpdate;
        }
    }

    /// <summary>
    /// Gets the current application version as a display string, including any pre-release label
    /// but excluding MinVer's <c>+commitsha</c> build metadata.
    /// </summary>
    /// <returns>The current version string, or <see langword="null"/> if it could not be determined
    /// (e.g. running from an unpublished build with no informational version attribute).</returns>
    public static string? GetCurrentVersionDisplayString()
    {
        var informationalVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return null;
        }

        var plusIndex = informationalVersion.IndexOf('+');
        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}

/// <summary>
/// Represents the outcome of an <see cref="UpdateChecking.CheckForUpdateAsync"/> check.
/// </summary>
/// <param name="IsUpdateAvailable">Whether a newer release than the current one was found.</param>
/// <param name="LatestVersion">The latest published version, if it could be determined.</param>
/// <param name="ReleaseUrl">The page a user should visit to download the latest release.</param>
public sealed record UpdateCheckOutcome(bool IsUpdateAvailable, string? LatestVersion, string ReleaseUrl);
