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

    public List<HourlyActivity> GetHourlyActivity()
    {
        return hourlyActivityBuilder.Build(locator.GetSessionFiles());
    }

    public RecentActivitySummary GetRecentActivity(TimeSpan window)
    {
        return recentActivityBuilder.Build(locator.GetSessionFiles(), window);
    }

    public StatsCache GetStatsCache()
    {
        if (File.Exists(locator.StatsCachePath))
        {
            try
            {
                return statsCacheParser.Parse(locator.StatsCachePath);
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

        return statsCacheBuilder.Build(locator.GetSessionFiles());
    }
}