using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.Caching;

namespace AIUsageMonitor.Core.Services;

/// <summary>
/// Represents a service that provides data related to AI usage, including daily summaries, hourly activity, model distribution, period summaries, recent activity, session stats, and stats cache. The service uses an <see cref="IUsageProvider"/> to retrieve data and a <see cref="UsageAnalyzer"/> to analyze the data. It also caches the stats cache for improved performance and monitors changes to relevant files using <see cref="FileSystemWatcher"/> instances.
/// </summary>
public sealed class DataService : IDisposable
{
    /// <summary>
    /// The key used to store and retrieve the stats cache from the memory cache. This constant is used to ensure consistent access to the cached stats cache across different methods in the <see cref="DataService"/> class.
    /// </summary>
    private const string StatsCacheKey = "StatsCache";

    /// <summary>
    /// The usage analyzer used to analyze AI usage data. This field is initialized in the constructor and is used to perform various analyses on the stats cache, such as generating daily summaries, model distributions, period summaries, and session statistics.
    /// </summary>
    private readonly UsageAnalyzer _analyzer;

    /// <summary>
    /// The memory cache used to store the stats cache for improved performance. This field is initialized with a unique name and is used to cache the stats cache retrieved from the usage provider, allowing for faster access to the data without needing to repeatedly read from disk or perform expensive computations.
    /// </summary>
    private readonly MemoryCache _cache = new("StatsCacheCache");

    /// <summary>
    /// The expiration time for the cached stats cache. This field is initialized with a default value of 10 minutes and is used to determine how long the stats cache should be kept in memory before being considered stale and needing to be refreshed from the usage provider.
    /// </summary>
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The logger instance used for logging warnings and errors. This field is initialized in the constructor and is used to log important information, such as file changes detected by the file system watchers, to help with debugging and monitoring the behavior of the <see cref="DataService"/> class.
    /// </summary>
    private readonly ILogger<DataService> _logger;

    /// <summary>
    /// The usage provider used to retrieve AI usage data. This field is initialized in the constructor and is responsible for providing access to the underlying data sources, such as session transcripts and stats cache files, allowing the <see cref="DataService"/> to retrieve and analyze usage data as needed.
    /// </summary>
    private readonly IUsageProvider _provider;

    /// <summary>
    /// Tracks the latest session file write time, fed incrementally from <see cref="_sessionsWatcher"/>
    /// events so <see cref="Providers.Claude.ClaudeUsageProvider"/> can check staleness without
    /// re-scanning the projects directory.
    /// </summary>
    private readonly SessionActivityTracker _sessionActivityTracker;

    /// <summary>
    /// The session file cache used to cache parsed session transcript rows. This field is initialized in the constructor and is used to store the results of parsing session transcript files, allowing for faster access to the data without needing to repeatedly read and parse the files from disk.
    /// </summary>
    private readonly SessionFileCache _sessionFileCache;

    /// <summary>
    /// The file system watcher used to monitor changes to session transcript files. This field is initialized in the constructor if the usage provider is a Claude usage provider and the stats cache file does not exist. The watcher listens for changes to JSONL files in the projects directory and clears the cached stats cache when changes are detected, ensuring that the service always has access to up-to-date data.
    /// </summary>
    private readonly FileSystemWatcher? _sessionsWatcher;

    /// <summary>
    /// The file system watcher used to monitor changes to the stats cache file. This field is initialized in the constructor if the usage provider is a Claude usage provider and the stats cache file exists. The watcher listens for changes to the stats-cache.json file and clears the cached stats cache when changes are detected, ensuring that the service always has access to up-to-date data.
    /// </summary>
    private readonly FileSystemWatcher? _statsCacheWatcher;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataService"/> class with the specified usage provider and usage analyzer. The constructor sets up file system watchers to monitor changes to relevant files, such as the stats cache and session transcripts, and clears the cached stats cache when changes are detected.
    /// </summary>
    /// <param name="provider">The usage provider used to retrieve AI usage data.</param>
    /// <param name="analyzer">The usage analyzer used to analyze AI usage data.</param>
    /// <param name="sessionFileCache">The session file cache used to cache parsed session transcript rows.</param>
    /// <param name="sessionActivityTracker">Tracks the latest session file write time from file-system watcher events.</param>
    /// <param name="logger">The logger instance used for logging warnings and errors.</param>
    public DataService(
        IUsageProvider provider,
        UsageAnalyzer analyzer,
        SessionFileCache sessionFileCache,
        SessionActivityTracker sessionActivityTracker,
        ILogger<DataService> logger)
    {
        _provider = provider;
        _analyzer = analyzer;
        _sessionFileCache = sessionFileCache;
        _sessionActivityTracker = sessionActivityTracker;
        _logger = logger;

        if (provider is ClaudeUsageProvider claudeProvider)
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
                _statsCacheWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Error watching stats-cache.json");
            }

            var projectsDir = claudeProvider.ProjectsDir;
            if (Directory.Exists(projectsDir))
            {
                _sessionsWatcher = new(projectsDir, "*.jsonl")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _sessionsWatcher.Changed += SessionsWatcher_Changed;
                _sessionsWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Error watching session files");
            }
        }
    }

    /// <summary>
    /// Disposes of the resources used by the <see cref="DataService"/> instance, including the file system watchers and the memory cache. This method should be called when the service is no longer needed to release unmanaged resources and prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        _statsCacheWatcher?.Dispose();
        _sessionsWatcher?.Dispose();
        _cache.Dispose();
    }

    /// <summary>
    /// Gets the daily summary for the specified date, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="date">The date for which to retrieve the daily summary.</param>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="DailySummary"/> for the specified date, or <see langword="null"/> if no activity was recorded for that date.</returns>
    public DailySummary? GetDailySummary(DateOnly date, IProgress<int>? progress = null)
    {
        return _analyzer.GetDailySummary(GetStatsCache(progress), date);
    }

    /// <summary>
    /// Gets the hourly activity, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>A list of <see cref="HourlyActivity"/> representing the hourly activity.</returns>
    public List<HourlyActivity> GetHourlyActivity(IProgress<int>? progress = null)
    {
        return _provider.GetHourlyActivity(progress);
    }

    /// <summary>
    /// Gets the model distribution, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>A list of <see cref="ModelDistribution"/> representing the model distribution.</returns>
    public List<ModelDistribution> GetModelDistribution(IProgress<int>? progress = null)
    {
        return _analyzer.GetModelDistribution(GetStatsCache(progress));
    }

    /// <summary>
    /// Gets the period summary for the specified date range, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="from">The start date of the period.</param>
    /// <param name="to">The end date of the period.</param>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="PeriodSummary"/> for the specified date range.</returns>
    public PeriodSummary GetPeriodSummary(DateOnly from, DateOnly to, IProgress<int>? progress = null)
    {
        return _analyzer.GetPeriodSummary(GetStatsCache(progress), from, to);
    }

    /// <summary>
    /// Gets the recent activity summary for the specified time window, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="window">The time window for which to retrieve recent activity.</param>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="RecentActivitySummary"/> for the specified time window.</returns>
    public RecentActivitySummary GetRecentActivity(TimeSpan window, IProgress<int>? progress = null)
    {
        return _provider.GetRecentActivity(window, progress);
    }

    /// <summary>
    /// Gets the session statistics, using the cached stats cache if available. If the stats cache is not cached, it retrieves it from the usage provider and caches it for future use. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="SessionStats"/> representing the session statistics.</returns>
    public SessionStats GetSessionStats(IProgress<int>? progress = null)
    {
        return _analyzer.GetSessionStats(GetStatsCache(progress));
    }

    /// <summary>
    /// Gets the stats cache, either from the memory cache or by retrieving it from the usage provider if not cached. The method also allows for progress reporting during the retrieval of the stats cache.
    /// </summary>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="StatsCache"/> representing the stats cache.</returns>
    public StatsCache GetStatsCache(IProgress<int>? progress = null)
    {
        if (_cache.Get(StatsCacheKey) is StatsCache cached)
        {
            progress?.Report(100);
            return cached;
        }

        var stats = _provider.GetStatsCache(progress);
        _cache.Set(StatsCacheKey, stats, DateTimeOffset.UtcNow.Add(_cacheExpiration));
        return stats;
    }

    /// <summary>
    /// Handles changes to session transcript files by updating the session file cache accordingly. When a session transcript file is changed or created, it is added to the cache, and when a session transcript file is deleted, it is removed from the cache. This ensures that the session file cache remains up-to-date with the latest session transcript data.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="FileSystemEventArgs"/> that contains the event data.</param>
    private void SessionsWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        _logger.LogTrace("Session file change detected: {ChangeType} - {FullPath}", e.ChangeType, e.FullPath);

        _cache.Remove(StatsCacheKey);

        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Changed:
            case WatcherChangeTypes.Created:
                _sessionFileCache.Set(e.FullPath);
                _sessionActivityTracker.Observe(File.GetLastWriteTimeUtc(e.FullPath));
                break;

            case WatcherChangeTypes.Deleted:
                _sessionFileCache.Remove(e.FullPath);
                break;
        }
    }
}