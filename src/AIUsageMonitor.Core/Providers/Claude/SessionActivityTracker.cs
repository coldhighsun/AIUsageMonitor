namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Tracks the most recent write time across all Claude session transcript files (<c>*.jsonl</c>)
/// without repeatedly re-scanning the projects directory. The first access to <see cref="LatestWriteUtc"/>
/// takes a one-time baseline scan; after that, callers who already observe file-system change events
/// (e.g. <see cref="Services.DataService"/>'s session-file watcher) feed updates via <see cref="Observe"/>
/// in O(1), so no further scans are needed.
/// </summary>
/// <param name="locator">Locates Claude session transcript files on disk, used only for the initial baseline scan.</param>
public sealed class SessionActivityTracker(ClaudeDataLocator locator)
{
    private readonly Lock _lock = new();
    private DateTime? _latestWriteUtc;

    /// <summary>
    /// Gets the latest known write time (UTC) across all session transcript files. Triggers a one-time
    /// directory scan on first access if no write has been observed yet via <see cref="Observe"/>.
    /// </summary>
    public DateTime LatestWriteUtc
    {
        get
        {
            lock (_lock)
            {
                _latestWriteUtc ??= ScanForLatestWriteUtc();
                return _latestWriteUtc.Value;
            }
        }
    }

    /// <summary>
    /// Records a newly observed write time, advancing <see cref="LatestWriteUtc"/> if it is more recent
    /// than what's currently known.
    /// </summary>
    /// <param name="writeTimeUtc">The write time (UTC) observed for a session transcript file.</param>
    public void Observe(DateTime writeTimeUtc)
    {
        lock (_lock)
        {
            if (_latestWriteUtc is null || writeTimeUtc > _latestWriteUtc)
            {
                _latestWriteUtc = writeTimeUtc;
            }
        }
    }

    private DateTime ScanForLatestWriteUtc()
    {
        var files = locator.GetSessionFiles();
        return files.Count == 0 ? DateTime.MinValue : files.Max(File.GetLastWriteTimeUtc);
    }
}