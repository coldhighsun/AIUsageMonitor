using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;

namespace AIUsageMonitor.Core.Providers.Claude;

public sealed class ClaudeUsageProvider(
    ClaudeDataLocator locator,
    StatsCacheParser statsCacheParser,
    StatsCacheBuilder statsCacheBuilder,
    RecentActivityBuilder recentActivityBuilder,
    HourlyActivityBuilder hourlyActivityBuilder,
    ILogger<ClaudeUsageProvider> logger) : IUsageProvider
{
    public string Name => "Claude";

    public string ProjectsDir => locator.ProjectsDir;
    public string StatsCachePath => locator.StatsCachePath;

    public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null)
    {
        return hourlyActivityBuilder.Build(locator.GetSessionFiles(), progress);
    }

    public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
    {
        return recentActivityBuilder.Build(locator.GetSessionFiles(), window, progress);
    }

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