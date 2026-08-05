using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers;

/// <summary>
/// Defines an interface for a usage provider that can retrieve hourly activity, recent activity summary, and stats cache data.
/// </summary>
public interface IUsageProvider
{
    /// <summary>
    /// Gets the name of the usage provider.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Retrieves a list of hourly activity data.
    /// </summary>
    /// <param name="progress">An optional progress reporter.</param>
    /// <returns>A list of <see cref="HourlyActivity"/> objects.</returns>
    List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null);

    /// <summary>
    /// Retrieves a summary of recent activity within the specified time window.
    /// </summary>
    /// <param name="window">The time window for which to retrieve recent activity.</param>
    /// <param name="progress">An optional progress reporter.</param>
    /// <returns>A <see cref="RecentActivitySummary"/> object.</returns>
    RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null);

    /// <summary>
    /// Retrieves a stats cache containing various usage statistics.
    /// </summary>
    /// <param name="progress">An optional progress reporter.</param>
    /// <returns>A <see cref="StatsCache"/> object.</returns>
    StatsCache GetStatsCache(IProgress<int>? progress = null);
}