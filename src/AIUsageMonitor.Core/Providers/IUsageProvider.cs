using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers;

public interface IUsageProvider
{
    string Name { get; }

    List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null);

    RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null);

    StatsCache GetStatsCache(IProgress<int>? progress = null);
}