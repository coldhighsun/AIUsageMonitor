namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Locates Claude Code data files and directories on disk, such as history logs,
/// project session files, and the stats cache.
/// </summary>
/// <param name="claudeDir">
/// Optional path to the Claude data directory. When <see langword="null"/>, defaults to
/// the <c>.claude</c> folder under the current user's profile directory.
/// </param>
/// <param name="timeProvider">The clock used to age the cached session file listing; defaults to the system clock.</param>
public sealed class ClaudeDataLocator(string? claudeDir = null, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// How long a cached session file listing is trusted even if no change event arrived, as a safety net
    /// against missed file-system notifications.
    /// </summary>
    public static readonly TimeSpan ListingMaxAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The Claude Code data directory this locator reads from.
    /// </summary>
    private readonly string _claudeDir =
        claudeDir ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// The clock used to age the cached listing.
    /// </summary>
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Guards the cached listing state below.
    /// </summary>
    private readonly Lock _listingLock = new();

    /// <summary>
    /// The set of known session files, kept in sync with change events while tracking is enabled;
    /// <see langword="null"/> when no listing is cached.
    /// </summary>
    private HashSet<string>? _knownFiles;

    /// <summary>
    /// The immutable snapshot handed to callers; replaced (never modified) whenever <see cref="_knownFiles"/> changes.
    /// </summary>
    private IReadOnlyList<string> _snapshot = [];

    /// <summary>
    /// The time <see cref="_knownFiles"/> was last built by a full directory scan.
    /// </summary>
    private DateTimeOffset _scannedAt;

    /// <summary>
    /// Whether a change-event source feeds <see cref="NotifyFileCreated"/> and friends, which is what makes caching safe.
    /// </summary>
    private bool _tracking;

    /// <summary>
    /// Gets the root Claude data directory (e.g. <c>%USERPROFILE%\.claude</c>).
    /// </summary>
    public string ClaudeDir => _claudeDir;

    /// <summary>
    /// Gets the full path to the <c>history.jsonl</c> file containing Claude command history.
    /// </summary>
    public string HistoryPath => Path.Combine(_claudeDir, "history.jsonl");

    /// <summary>
    /// Gets the full path to the <c>projects</c> directory containing per-project session data.
    /// </summary>
    public string ProjectsDir => Path.Combine(_claudeDir, "projects");

    /// <summary>
    /// Gets the full path to the <c>stats-cache.json</c> file used to cache computed usage statistics.
    /// </summary>
    public string StatsCachePath => Path.Combine(_claudeDir, "stats-cache.json");

    /// <summary>
    /// Enumerates the project directories under <see cref="ProjectsDir"/>.
    /// </summary>
    /// <returns>
    /// A list of tuples containing each project's encoded directory name and its full path,
    /// or an empty list if <see cref="ProjectsDir"/> does not exist.
    /// </returns>
    public IReadOnlyList<(string EncodedName, string FullPath)> GetProjectDirectories()
    {
        if (!Directory.Exists(ProjectsDir))
        {
            return [];
        }

        return Directory.EnumerateDirectories(ProjectsDir)
            .Select(d => (Path.GetFileName(d), d))
            .ToList();
    }

    /// <summary>
    /// Finds all session log files (<c>*.jsonl</c>) recursively under <see cref="ProjectsDir"/>.
    /// </summary>
    /// <remarks>
    /// A recursive scan of a large projects tree is slow (hundreds of directories), so once
    /// <see cref="EnableChangeTracking"/> has been called the listing is cached and kept current from file-system
    /// events instead of being rescanned on every call. Without tracking every call scans the disk.
    /// </remarks>
    /// <returns>
    /// A list of full paths to session files, or an empty list if <see cref="ProjectsDir"/> does not exist.
    /// The returned list is a snapshot and is never modified afterwards.
    /// </returns>
    public IReadOnlyList<string> GetSessionFiles()
    {
        if (!_tracking)
        {
            return ScanSessionFiles();
        }

        lock (_listingLock)
        {
            if (_knownFiles is null || _timeProvider.GetUtcNow() - _scannedAt >= ListingMaxAge)
            {
                // Scanned while holding the lock so that events raised meanwhile are applied after the scan, not lost.
                var scanned = ScanSessionFiles();
                _knownFiles = new HashSet<string>(scanned, PathComparer);
                _snapshot = scanned;
                _scannedAt = _timeProvider.GetUtcNow();
            }

            return _snapshot;
        }
    }

    /// <summary>
    /// Declares that the caller feeds every session file creation, deletion and rename (and any lost-notification
    /// condition) into this locator, which allows <see cref="GetSessionFiles"/> to cache its listing. Call it only
    /// after the file-system watcher is already raising events.
    /// </summary>
    public void EnableChangeTracking()
    {
        lock (_listingLock)
        {
            _tracking = true;
            _knownFiles = null;
        }
    }

    /// <summary>
    /// Records that a session file appeared.
    /// </summary>
    /// <param name="path">The full path of the new session file.</param>
    public void NotifyFileCreated(string path)
    {
        UpdateListing(files => files.Add(path));
    }

    /// <summary>
    /// Records that a session file disappeared.
    /// </summary>
    /// <param name="path">The full path of the removed session file.</param>
    public void NotifyFileDeleted(string path)
    {
        UpdateListing(files => files.Remove(path));
    }

    /// <summary>
    /// Discards the cached listing so the next <see cref="GetSessionFiles"/> rescans the disk. Use it when
    /// notifications may have been lost (e.g. the watcher's buffer overflowed).
    /// </summary>
    public void InvalidateSessionFiles()
    {
        lock (_listingLock)
        {
            _knownFiles = null;
        }
    }

    /// <summary>
    /// Gets the comparer for session file paths: case-insensitive on Windows, where event paths may differ in case.
    /// </summary>
    private static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Applies a change to the cached listing, if there is one, and publishes a new snapshot when it changed.
    /// </summary>
    /// <param name="change">Mutates the known set and returns whether it changed.</param>
    private void UpdateListing(Func<HashSet<string>, bool> change)
    {
        lock (_listingLock)
        {
            // With no cached listing the next scan sees the current disk state anyway.
            if (_knownFiles is not null && change(_knownFiles))
            {
                _snapshot = [.. _knownFiles];
            }
        }
    }

    /// <summary>
    /// Scans <see cref="ProjectsDir"/> recursively for session files.
    /// </summary>
    /// <returns>The full paths of all session files, or an empty list if the directory does not exist.</returns>
    private string[] ScanSessionFiles()
    {
        if (!Directory.Exists(ProjectsDir))
        {
            return [];
        }

        return Directory.GetFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories);
    }
}