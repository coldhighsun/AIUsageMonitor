using System.Runtime.Caching;
using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Services;

public sealed class DataService : IDisposable
{
    private const string StatsCacheKey = "StatsCache";

    private readonly UsageAnalyzer _analyzer;
    private readonly MemoryCache _cache = new("StatsCacheCache");
    private readonly IUsageProvider _provider;
    private readonly FileSystemWatcher? _sessionsWatcher;
    private readonly FileSystemWatcher? _statsCacheWatcher;

    public DataService(IUsageProvider provider, UsageAnalyzer analyzer)
    {
        _provider = provider;
        _analyzer = analyzer;

        if (provider is Providers.Claude.ClaudeUsageProvider claudeProvider)
        {
            var cacheDir = Path.GetDirectoryName(claudeProvider.StatsCachePath);
            if (cacheDir is not null && Directory.Exists(cacheDir))
            {
                _statsCacheWatcher = new(cacheDir, "stats-cache.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                _statsCacheWatcher.Changed += (_, _) => _cache.Remove(StatsCacheKey);
            }

            if (!File.Exists(claudeProvider.StatsCachePath))
            {
                var projectsDir = claudeProvider.ProjectsDir;
                if (Directory.Exists(projectsDir))
                {
                    _sessionsWatcher = new(projectsDir, "*.jsonl")
                    {
                        NotifyFilter = NotifyFilters.LastWrite,
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };
                    _sessionsWatcher.Changed += (_, _) => _cache.Remove(StatsCacheKey);
                }
            }
        }
    }

    public void Dispose()
    {
        _statsCacheWatcher?.Dispose();
        _sessionsWatcher?.Dispose();
        _cache.Dispose();
    }

    public DailySummary? GetDailySummary(DateOnly date)
    {
        return _analyzer.GetDailySummary(GetStatsCache(), date);
    }

    public List<HourlyActivity> GetHourlyActivity()
    {
        return _provider.GetHourlyActivity();
    }

    public List<ModelDistribution> GetModelDistribution()
    {
        return _analyzer.GetModelDistribution(GetStatsCache());
    }

    public PeriodSummary GetPeriodSummary(DateOnly from, DateOnly to)
    {
        return _analyzer.GetPeriodSummary(GetStatsCache(), from, to);
    }

    public RecentActivitySummary GetRecentActivity(TimeSpan window)
    {
        return _provider.GetRecentActivity(window);
    }

    public SessionStats GetSessionStats()
    {
        return _analyzer.GetSessionStats(GetStatsCache());
    }

    public StatsCache GetStatsCache()
    {
        if (_cache.Get(StatsCacheKey) is StatsCache cached)
        {
            return cached;
        }

        var stats = _provider.GetStatsCache();
        _cache.Set(StatsCacheKey, stats, DateTimeOffset.UtcNow.AddMinutes(1));
        return stats;
    }
}