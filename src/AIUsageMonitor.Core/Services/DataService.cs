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
    /// How long the session watcher must be quiet before the changed transcripts are refreshed in the background.
    /// </summary>
    private static readonly TimeSpan RefreshDebounceDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The longest a background refresh is postponed while change events keep arriving.
    /// </summary>
    private static readonly TimeSpan RefreshDebounceMaxWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The usage analyzer used to analyze AI usage data. This field is initialized in the constructor and is used to perform various analyses on the stats cache, such as generating daily summaries, model distributions, period summaries, and session statistics.
    /// </summary>
    private readonly UsageAnalyzer _analyzer;

    /// <summary>
    /// The memory cache used to store the stats cache for improved performance. This field is initialized with a unique name and is used to cache the stats cache retrieved from the usage provider, allowing for faster access to the data without needing to repeatedly read from disk or perform expensive computations.
    /// </summary>
    private readonly MemoryCache _cache = new("StatsCacheCache");

    /// <summary>
    /// Guards <see cref="_statsCacheVersion"/> together with the stats cache entry, so that checking the version and
    /// storing a freshly computed stats cache cannot interleave with an invalidation.
    /// </summary>
    private readonly Lock _statsCacheGate = new();

    /// <summary>
    /// Incremented on every invalidation of the stats cache; a computation that started under an older value is stale.
    /// </summary>
    private long _statsCacheVersion;

    /// <summary>
    /// How long a stats cache that was invalidated while it was being computed is still served.
    /// </summary>
    private static readonly TimeSpan StaleResultLifetime = TimeSpan.FromSeconds(2);

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
    /// Coalesces the bursts of change events raised while a transcript is being written to, so that each changed
    /// file is refreshed in the background once per burst rather than once per event.
    /// </summary>
    private readonly PathChangeDebouncer? _refreshDebouncer;

    /// <summary>
    /// Watches for directories under the projects folder being created, deleted or renamed, which the
    /// transcript watcher cannot see because of its <c>*.jsonl</c> filter.
    /// </summary>
    private readonly FileSystemWatcher? _directoriesWatcher;

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
                _statsCacheWatcher.Changed += (_, _) => InvalidateStatsCache();
                _statsCacheWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Error watching stats-cache.json");
            }

            var projectsDir = claudeProvider.ProjectsDir;
            if (Directory.Exists(projectsDir))
            {
                _refreshDebouncer = new(RefreshDebounceDelay, RefreshDebounceMaxWait, RefreshChangedFiles, TimeProvider.System);
                _sessionsWatcher = new(projectsDir, "*.jsonl")
                {
                    // FileName is required for the watcher to report files being created, deleted and renamed.
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _sessionsWatcher.Changed += SessionsWatcher_Changed;
                _sessionsWatcher.Created += SessionsWatcher_Changed;
                _sessionsWatcher.Deleted += SessionsWatcher_Changed;
                _sessionsWatcher.Renamed += SessionsWatcher_Renamed;
                _sessionsWatcher.Error += SessionsWatcher_Error;

                // The file watcher's "*.jsonl" filter hides directory renames and moves, which relocate every
                // transcript beneath them, so directories get a watcher of their own.
                _directoriesWatcher = new(projectsDir)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _directoriesWatcher.Created += DirectoriesWatcher_Changed;
                _directoriesWatcher.Deleted += DirectoriesWatcher_Changed;
                _directoriesWatcher.Renamed += DirectoriesWatcher_Changed;
                _directoriesWatcher.Error += SessionsWatcher_Error;

                // Only now that events are flowing may the file listing be cached: any later change reaches it.
                claudeProvider.Locator.EnableChangeTracking();
                _sessionFileCache.EnableWriteTimeTracking();
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
        _directoriesWatcher?.Dispose();
        _refreshDebouncer?.Dispose();
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
    /// Gets a summary of the current 5-hour session window.
    /// </summary>
    /// <param name="sessionResetAt">
    /// The real reset time of the current window, if known; otherwise the window is estimated
    /// locally or reported as unknown.
    /// </param>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="UsageWindowSummary"/> for the current session window.</returns>
    public UsageWindowSummary GetCurrentSessionWindow(DateTimeOffset? sessionResetAt, IProgress<int>? progress = null)
    {
        return _provider.GetCurrentSessionWindow(sessionResetAt, progress);
    }

    /// <summary>
    /// Gets a summary of the current weekly window.
    /// </summary>
    /// <param name="anchor">The real weekly reset day and local time-of-day, if known; otherwise the window is estimated locally.</param>
    /// <param name="progress">An optional progress reporter to report the progress of the operation.</param>
    /// <returns>The <see cref="UsageWindowSummary"/> for the current weekly window.</returns>
    public UsageWindowSummary GetWeekWindow((DayOfWeek Day, TimeSpan TimeOfDay)? anchor, IProgress<int>? progress = null)
    {
        return _provider.GetWeekWindow(anchor, progress);
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

        var version = Volatile.Read(ref _statsCacheVersion);
        var stats = _provider.GetStatsCache(progress);
        lock (_statsCacheGate)
        {
            // An invalidation that arrived while the stats were being computed means they may already be out of
            // date, so they are only kept briefly: long enough for the other reads of the same refresh to share
            // them, short enough that the staleness stays negligible. Any later invalidation drops them at once.
            var lifetime = version == _statsCacheVersion ? _cacheExpiration : StaleResultLifetime;
            _cache.Set(StatsCacheKey, stats, DateTimeOffset.UtcNow.Add(lifetime));
        }

        return stats;
    }

    /// <summary>
    /// Drops the cached stats cache and marks any computation that is currently in flight as stale, so that its
    /// result is not cached after the fact.
    /// </summary>
    internal void InvalidateStatsCache()
    {
        lock (_statsCacheGate)
        {
            _statsCacheVersion++;
            _cache.Remove(StatsCacheKey);
        }
    }


    /// <summary>
    /// Handles a session transcript being changed, created or deleted. The cheap, correctness-relevant bookkeeping
    /// (invalidating the stats cache, the file listing and the latest-write tracker) happens immediately; refreshing
    /// the parsed rows is debounced and done in the background, because a transcript that is being written to raises
    /// an event for nearly every append.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="FileSystemEventArgs"/> that contains the event data.</param>
    private void SessionsWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        _logger.LogTrace("Session file change detected: {ChangeType} - {FullPath}", e.ChangeType, e.FullPath);

        InvalidateStatsCache();

        switch (e.ChangeType)
        {
            case WatcherChangeTypes.Changed:
            case WatcherChangeTypes.Created:
                if (e.ChangeType == WatcherChangeTypes.Created)
                {
                    (_provider as ClaudeUsageProvider)?.Locator.NotifyFileCreated(e.FullPath);
                }

                var lastWriteUtc = File.GetLastWriteTimeUtc(e.FullPath);
                _sessionFileCache.NoteWritten(e.FullPath, lastWriteUtc);
                _sessionActivityTracker.Observe(lastWriteUtc);
                _refreshDebouncer?.Add(e.FullPath);
                break;

            case WatcherChangeTypes.Deleted:
                (_provider as ClaudeUsageProvider)?.Locator.NotifyFileDeleted(e.FullPath);
                _sessionFileCache.Remove(e.FullPath);
                break;
        }
    }

    /// <summary>
    /// Handles a session transcript being renamed: the old name disappears and the new one appears.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="RenamedEventArgs"/> that contains the old and new paths.</param>
    private void SessionsWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        _logger.LogTrace("Session file renamed: {OldPath} -> {FullPath}", e.OldFullPath, e.FullPath);

        InvalidateStatsCache();
        _sessionFileCache.Remove(e.OldFullPath);

        var locator = (_provider as ClaudeUsageProvider)?.Locator;
        locator?.NotifyFileDeleted(e.OldFullPath);
        if (e.FullPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            locator?.NotifyFileCreated(e.FullPath);
            var lastWriteUtc = File.GetLastWriteTimeUtc(e.FullPath);
            _sessionFileCache.NoteWritten(e.FullPath, lastWriteUtc);
            _sessionActivityTracker.Observe(lastWriteUtc);
            _refreshDebouncer?.Add(e.FullPath);
        }
    }

    /// <summary>
    /// Handles a directory under the projects folder being created, deleted, renamed or moved. Such a change can
    /// relocate any number of transcripts without a per-file event, so the cached file listing is dropped.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">A <see cref="FileSystemEventArgs"/> that contains the event data.</param>
    private void DirectoriesWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        _logger.LogTrace("Project directory change detected: {ChangeType} - {FullPath}", e.ChangeType, e.FullPath);

        (_provider as ClaudeUsageProvider)?.Locator.InvalidateSessionFiles();
        InvalidateStatsCache();
    }

    /// <summary>
    /// Handles the session watcher losing events (e.g. its buffer overflowed): nothing cached from events can be
    /// trusted any more, so the file listing and stats cache are dropped and rebuilt on next use.
    /// </summary>
    /// <param name="sender">The source of the event.</param>
    /// <param name="e">An <see cref="ErrorEventArgs"/> that contains the failure.</param>
    private void SessionsWatcher_Error(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "Error watching session files; discarding cached file listing and write times");

        (_provider as ClaudeUsageProvider)?.Locator.InvalidateSessionFiles();
        _sessionFileCache.ForgetWriteTimes();
        InvalidateStatsCache();
    }

    /// <summary>
    /// Refreshes the parsed rows of transcripts that changed, off the watcher thread. Failures are logged and
    /// swallowed: this runs on a timer thread, where an escaping exception would terminate the process, and the
    /// rows are refreshed again on demand anyway.
    /// </summary>
    /// <param name="paths">The distinct transcripts that changed during the last burst of events.</param>
    private void RefreshChangedFiles(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                _sessionFileCache.Set(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background refresh of {File} failed", path);
            }
        }
    }
}
