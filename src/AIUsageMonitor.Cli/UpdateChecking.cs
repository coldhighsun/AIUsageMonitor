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
    /// Checks whether a newer release than the current one is available, answering from the on-disk cache when it
    /// is still fresh and otherwise asking GitHub and refreshing the cache.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <param name="logger">An optional logger used to record failed update checks.</param>
    /// <returns>
    /// The check outcome. Any network, timeout, or parsing failure is treated as "no update
    /// available" rather than propagated, since this check must never break the command it runs
    /// alongside.
    /// </returns>
    public static Task<UpdateCheckOutcome> CheckForUpdateAsync(
        CancellationToken cancellationToken = default, ILogger? logger = null)
    {
        var cache = new UpdateCheckCache(UpdateCheckCache.DefaultPath, TimeProvider.System);

        return CheckForUpdateAsync(
            GetCurrentVersionDisplayString(),
            cache,
            (version, ct) => FetchLatestAsync(version, logger, ct),
            cancellationToken);
    }

    /// <summary>
    /// Core of the update check with its collaborators injected: consults <paramref name="cache"/> first, and only
    /// calls <paramref name="fetchAsync"/> on a miss, remembering the result (including failures, briefly).
    /// </summary>
    /// <param name="currentVersion">The running version, or <see langword="null"/> if unknown (no check is made).</param>
    /// <param name="cache">The cache of previous check results.</param>
    /// <param name="fetchAsync">Asks GitHub for the latest release; returns <see langword="null"/> when the request failed.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The check outcome; "no update" when nothing could be determined.</returns>
    internal static async Task<UpdateCheckOutcome> CheckForUpdateAsync(
        string? currentVersion,
        UpdateCheckCache cache,
        Func<string, CancellationToken, Task<UpdateCheckOutcome?>> fetchAsync,
        CancellationToken cancellationToken)
    {
        var noUpdate = new UpdateCheckOutcome(false, null, ReleaseUrl);
        if (currentVersion is null)
        {
            return noUpdate;
        }

        if (cache.TryGet(currentVersion) is { } cached)
        {
            return cached;
        }

        UpdateCheckOutcome? fetched;
        try
        {
            fetched = await fetchAsync(currentVersion, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // An abandoned check says nothing about the network, so it is not remembered.
            return noUpdate;
        }

        cache.Save(currentVersion, fetched ?? noUpdate, succeeded: fetched is not null);

        return fetched ?? noUpdate;
    }

    /// <summary>
    /// Asks GitHub for the latest release.
    /// </summary>
    /// <param name="currentVersion">The running version to compare against.</param>
    /// <param name="logger">An optional logger used to record a failed check.</param>
    /// <param name="cancellationToken">A token to cancel the request.</param>
    /// <returns>The outcome, or <see langword="null"/> if GitHub could not be reached or answered unusably.</returns>
    private static async Task<UpdateCheckOutcome?> FetchLatestAsync(
        string currentVersion, ILogger? logger, CancellationToken cancellationToken)
    {
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

                return null;
            }

            return check.IsUpdateAvailable
                ? new UpdateCheckOutcome(true, check.Update!.Version.ToString(), ReleaseUrl)
                : new UpdateCheckOutcome(false, null, ReleaseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Update check timed out");

            return null;
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
