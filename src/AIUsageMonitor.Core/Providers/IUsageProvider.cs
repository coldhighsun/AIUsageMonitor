using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers;

public interface IUsageProvider
{
    string Name { get; }

    List<HourlyActivity> GetHourlyActivity();

    RecentActivitySummary GetRecentActivity(TimeSpan window);

    StatsCache GetStatsCache();
}