using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Provides Claude usage data by parsing local session transcripts and the stats cache file.
/// </summary>
/// <param name="locator">Locates Claude data files (session transcripts and stats cache) on disk.</param>
/// <param name="statsCacheParser">Parses the stats-cache.json file into a <see cref="StatsCache"/>.</param>
/// <param name="statsCacheBuilder">Builds a <see cref="StatsCache"/> from session transcripts when the cache is missing or stale.</param>
/// <param name="recentActivityBuilder">Builds a summary of recent activity from session transcripts.</param>
/// <param name="hourlyActivityBuilder">Builds hourly activity data from session transcripts.</param>
/// <param name="logger">Logger used to report cache fallback diagnostics.</param>
public sealed class ClaudeUsageProvider(
    ClaudeDataLocator locator,
    StatsCacheParser statsCacheParser,
    StatsCacheBuilder statsCacheBuilder,
    RecentActivityBuilder recentActivityBuilder,
    HourlyActivityBuilder hourlyActivityBuilder,
    ILogger<ClaudeUsageProvider> logger) : IUsageProvider
{
    /// <summary>Gets the display name of this usage provider.</summary>
    public string Name => "Claude";

    /// <summary>Gets the directory containing Claude project/session data.</summary>
    public string ProjectsDir => locator.ProjectsDir;

    /// <summary>Gets the path to the Claude stats-cache.json file.</summary>
    public string StatsCachePath => locator.StatsCachePath;

    /// <summary>
    /// Builds hourly activity data from Claude session transcripts.
    /// </summary>
    /// <param name="progress">Optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A list of <see cref="HourlyActivity"/> entries.</returns>
    public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null)
    {
        return hourlyActivityBuilder.Build(locator.GetSessionFiles(), progress);
    }

    /// <summary>
    /// Builds a summary of recent Claude activity within the specified time window.
    /// </summary>
    /// <param name="window">The time window to look back over, relative to now.</param>
    /// <param name="progress">Optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>A <see cref="RecentActivitySummary"/> describing recent activity.</returns>
    public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
    {
        return recentActivityBuilder.Build(locator.GetSessionFiles(), window, progress);
    }

    /// <summary>
    /// Gets the current usage stats cache, using stats-cache.json when present and up to date,
    /// or computing it from session transcripts otherwise.
    /// </summary>
    /// <param name="progress">Optional progress reporter for tracking build progress (0-100).</param>
    /// <returns>The resolved <see cref="StatsCache"/>.</returns>
    public StatsCache GetStatsCache(IProgress<int>? progress = null)
    {
        if (File.Exists(locator.StatsCachePath))
        {
            try
            {
                var cache = statsCacheParser.Parse(locator.StatsCachePath);
                var today = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
                if (cache.LastComputedDate >= today)
                {
                    progress?.Report(100);
                    return cache;
                }

                logger.LogInformation(
                    "stats-cache.json at {Path} is stale (last computed {LastComputedDate}); computing usage from session transcripts instead",
                    locator.StatsCachePath, cache.LastComputedDate);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse stats-cache.json at {Path}; falling back to session transcripts", locator.StatsCachePath);
            }
        }
        else
        {
            logger.LogInformation("stats-cache.json not found at {Path}; computing usage from session transcripts instead", locator.StatsCachePath);
        }

        return statsCacheBuilder.Build(locator.GetSessionFiles(), progress);
    }
}