using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Caches each session transcript's parsed rows, so that re-aggregating stats only parses what actually changed.
/// Three layers keep the work small: an in-memory entry per file (for long-running processes such as <c>watch</c> and
/// the WPF app), an optional on-disk entry per file (so a new process starts warm), and incremental parsing - a
/// transcript that has only been appended to is extended by parsing just the new lines instead of being re-read.
/// </summary>
/// <param name="sessionParser">The <see cref="SessionParser"/> used to parse session transcript files.</param>
/// <param name="logger">The logger instance used for logging warnings when a transcript file cannot be read.</param>
/// <param name="diskCache">The persistent cache shared across processes, or <see langword="null"/> to cache in memory only.</param>
/// <param name="timeProvider">The clock used to age remembered write times; defaults to the system clock.</param>
public sealed class SessionFileCache(
    SessionParser sessionParser,
    ILogger<SessionFileCache> logger,
    SessionRowDiskCache? diskCache = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    /// How long remembered last-write times are trusted before being re-read from disk, as a safety net against
    /// lost file-system notifications.
    /// </summary>
    public static readonly TimeSpan WriteTimeMaxAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The shortest interval at which a continuously changing file is written back to the disk cache by one process.
    /// </summary>
    private static readonly TimeSpan DiskWriteInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The clock-skew tolerance applied when comparing a file's last-write time against a window start.
    /// </summary>
    private static readonly TimeSpan ModifiedTimeSlack = TimeSpan.FromHours(1);

    /// <summary>
    /// A thread-safe dictionary of the parsed state of each transcript, keyed by file path.
    /// </summary>
    private readonly ConcurrentDictionary<string, CachedSessionFile> _cache = new();

    /// <summary>
    /// When each file was last written to the disk cache by this process (as <see cref="Environment.TickCount64"/>),
    /// used to avoid rewriting the entry of a file that is being appended to on every refresh.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _lastDiskWriteTicks = new();

    /// <summary>
    /// The clock used to age remembered write times.
    /// </summary>
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The remembered last-write time (UTC ticks) of each transcript, used by <see cref="GetFilesModifiedSince"/>
    /// while <see cref="_trackWriteTimes"/> is on.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _writeTicks = new(ClaudeDataLocator.PathComparer);

    /// <summary>
    /// Whether every write is reported to <see cref="NoteWritten"/>, which is what makes remembering write times safe.
    /// </summary>
    private volatile bool _trackWriteTimes;

    /// <summary>
    /// When <see cref="_writeTicks"/> was last emptied (or tracking was enabled).
    /// </summary>
    private DateTimeOffset _writeTicksBuiltAt;

    /// <summary>
    /// One lock object per file, serializing concurrent refreshes of the same transcript.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _gates = new();

    /// <summary>
    /// Gets the parsed rows for a given session transcript file. If the file has not changed since the last read, returns the cached rows; otherwise, parses what changed (only the appended lines when possible) and updates the cache.
    /// If the file cannot be read (e.g. it is temporarily locked by another process), the previously cached rows are returned instead, if any.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    /// <returns>A list of <see cref="SessionMessage"/> objects representing the parsed rows of the session transcript.</returns>
    public IReadOnlyList<SessionMessage> GetRows(string filePath)
    {
        return Refresh(filePath)?.Rows ?? [];
    }

    /// <summary>
    /// Returns the files that may contain messages recorded at or after <paramref name="since"/>. A transcript
    /// line is never stamped later than the moment it is written, so a file whose last-write time is older than
    /// <paramref name="since"/> (allowing for a small clock-skew tolerance) cannot contain any such message and
    /// need not be opened at all.
    /// </summary>
    /// <remarks>
    /// With <see cref="EnableWriteTimeTracking"/> on, last-write times are remembered between calls (and kept
    /// current by <see cref="NoteWritten"/>) instead of being read from the disk for every file on every call.
    /// </remarks>
    /// <param name="filePaths">The candidate session transcript files.</param>
    /// <param name="since">The earliest message timestamp the caller is interested in.</param>
    /// <returns>The subset of <paramref name="filePaths"/> that could hold messages at or after <paramref name="since"/>.</returns>
    public IReadOnlyList<string> GetFilesModifiedSince(IReadOnlyList<string> filePaths, DateTimeOffset since)
    {
        var thresholdTicks = (since - ModifiedTimeSlack).UtcDateTime.Ticks;
        var result = new List<string>(filePaths.Count);
        var tracking = _trackWriteTimes && ResetStaleWriteTimes();

        foreach (var file in filePaths)
        {
            long ticks;
            if (!tracking)
            {
                ticks = File.GetLastWriteTimeUtc(file).Ticks;
            }
            else if (!_writeTicks.TryGetValue(file, out ticks))
            {
                ticks = File.GetLastWriteTimeUtc(file).Ticks;
                RememberWriteTime(file, ticks);
            }

            // A missing file reports the 1601 sentinel, so it is filtered out like any stale file.
            if (ticks >= thresholdTicks)
            {
                result.Add(file);
            }
        }

        return result;
    }

    /// <summary>
    /// Starts remembering each file's last-write time between <see cref="GetFilesModifiedSince"/> calls. Call it only
    /// once every write is also reported to <see cref="NoteWritten"/> (and lost notifications to
    /// <see cref="ForgetWriteTimes"/>), after the file-system watcher is already raising events: a remembered time is
    /// otherwise never refreshed, and a file written since would wrongly be treated as unchanged.
    /// </summary>
    public void EnableWriteTimeTracking()
    {
        _writeTicksBuiltAt = _clock.GetUtcNow();
        _trackWriteTimes = true;
    }

    /// <summary>
    /// Records that a transcript was written, so a file that had been idle is no longer skipped by
    /// <see cref="GetFilesModifiedSince"/>.
    /// </summary>
    /// <param name="filePath">The transcript that was written.</param>
    /// <param name="lastWriteUtc">The file's last-write time, as read when the event was handled.</param>
    public void NoteWritten(string filePath, DateTime lastWriteUtc)
    {
        if (_trackWriteTimes)
        {
            RememberWriteTime(filePath, lastWriteUtc.Ticks);
        }
    }

    /// <summary>
    /// Drops all remembered last-write times so the next <see cref="GetFilesModifiedSince"/> reads them from disk
    /// again. Use it when file-system notifications may have been lost.
    /// </summary>
    public void ForgetWriteTimes()
    {
        _writeTicks.Clear();
        _writeTicksBuiltAt = _clock.GetUtcNow();
    }

    /// <summary>
    /// Stores a last-write time, never moving a remembered time backwards: a stat that raced with a write event
    /// may be older than what the event already recorded.
    /// </summary>
    /// <param name="filePath">The transcript.</param>
    /// <param name="ticks">The last-write time, as UTC ticks.</param>
    private void RememberWriteTime(string filePath, long ticks)
    {
        _writeTicks.AddOrUpdate(filePath, ticks, (_, known) => Math.Max(known, ticks));
    }

    /// <summary>
    /// Forgets remembered write times once they are older than <see cref="WriteTimeMaxAge"/>, bounding how long a
    /// lost notification can leave a stale time behind.
    /// </summary>
    /// <returns>Always <see langword="true"/>, so it can be chained in a condition.</returns>
    private bool ResetStaleWriteTimes()
    {
        if (_clock.GetUtcNow() - _writeTicksBuiltAt >= WriteTimeMaxAge)
        {
            ForgetWriteTimes();
        }

        return true;
    }

    /// <summary>
    /// Parses (or refreshes) the rows of all given files in parallel so that the subsequent sequential
    /// aggregation passes are served entirely from the cache. Unchanged files are only stat-ed.
    /// </summary>
    /// <param name="filePaths">The session transcript files that are about to be read.</param>
    /// <param name="progress">An optional progress reporter (0-100) that is notified as files complete.</param>
    public void WarmUp(IReadOnlyList<string> filePaths, IProgress<int>? progress = null)
    {
        var completed = 0;
        var progressLock = new Lock();
        var total = filePaths.Count;

        Parallel.For(
            0,
            total,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
            index =>
            {
                try
                {
                    GetRows(filePaths[index]);
                }
                catch (Exception ex)
                {
                    // Best effort only: the sequential aggregation pass calls GetRows again and applies its own handling.
                    logger.LogDebug(ex, "Warm-up of {File} failed", filePaths[index]);
                }
                finally
                {
                    // Counted and reported under one lock so that reports are serialized and never go backwards:
                    // reporting outside it lets a thread with a smaller count overtake one with a larger count.
                    if (progress is not null)
                    {
                        lock (progressLock)
                        {
                            completed++;
                            progress.Report(completed * 100 / total);
                        }
                    }
                }
            });
    }

    /// <summary>
    /// Removes cached entries (in memory and on disk) for files that are no longer present in <paramref name="currentFiles"/>.
    /// </summary>
    /// <param name="currentFiles">The set of session transcript file paths that currently exist on disk.</param>
    public void Prune(IReadOnlyCollection<string> currentFiles)
    {
        var currentSet = currentFiles.ToHashSet();
        foreach (var key in _cache.Keys)
        {
            if (!currentSet.Contains(key))
            {
                _cache.TryRemove(key, out _);
                _lastDiskWriteTicks.TryRemove(key, out _);
                _gates.TryRemove(key, out _);
            }
        }

        foreach (var key in _writeTicks.Keys)
        {
            if (!currentSet.Contains(key))
            {
                _writeTicks.TryRemove(key, out _);
            }
        }

        diskCache?.Prune(currentFiles);
    }

    /// <summary>
    /// Removes the cached entry for a specific session transcript file, in memory and on disk, if it exists. This can be used to manually invalidate the cache for a particular file.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    public void Remove(string filePath)
    {
        _cache.TryRemove(filePath, out _);
        _lastDiskWriteTicks.TryRemove(filePath, out _);
        _gates.TryRemove(filePath, out _);
        _writeTicks.TryRemove(filePath, out _);
        diskCache?.Remove(filePath);
    }

    /// <summary>
    /// Refreshes the cached entry for a specific session transcript file. If the file cannot be read
    /// (e.g. it is temporarily locked by another process), the previously cached entry is left untouched so
    /// that no stats are lost; the file will be retried on the next change notification.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    public void Set(string filePath)
    {
        Refresh(filePath);
    }

    /// <summary>
    /// Brings the cached state of a transcript up to date with the file on disk.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    /// <returns>The up-to-date state, the last known state if the file cannot be read now, or <see langword="null"/> if there is none.</returns>
    private CachedSessionFile? Refresh(string filePath)
    {
        var info = new FileInfo(filePath);
        if (!info.Exists)
        {
            Remove(filePath);

            return null;
        }

        var length = info.Length;
        var lastWriteTicks = info.LastWriteTimeUtc.Ticks;

        if (_cache.TryGetValue(filePath, out var current) && current.Matches(length, lastWriteTicks))
        {
            return current;
        }

        // One refresh per file at a time: when the watcher's background refresh and a caller want the same
        // changed file, the second waits and then finds the entry up to date instead of parsing it again.
        lock (_gates.GetOrAdd(filePath, static _ => new object()))
        {
            // Re-stat now that the lock is held: while waiting, another refresh may have parsed a newer state, and
            // acting on the older size would make that state look un-extendable and force a full re-parse.
            info.Refresh();
            if (!info.Exists)
            {
                Remove(filePath);

                return null;
            }

            return RefreshLocked(filePath, info.Length, info.LastWriteTimeUtc.Ticks);
        }
    }

    /// <summary>
    /// Refreshes a transcript's cached state; the caller holds the file's gate.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    /// <param name="length">The file's current size.</param>
    /// <param name="lastWriteTicks">The file's current last-write time, as UTC ticks.</param>
    /// <returns>The up-to-date state, the last known state if the file cannot be read now, or <see langword="null"/> if there is none.</returns>
    private CachedSessionFile? RefreshLocked(string filePath, long length, long lastWriteTicks)
    {
        _cache.TryGetValue(filePath, out var known);
        if (known is not null && known.Matches(length, lastWriteTicks))
        {
            return known;
        }

        if (known is null && diskCache?.TryLoad(filePath) is { } stored)
        {
            known = stored;
            if (stored.Matches(length, lastWriteTicks))
            {
                _cache[filePath] = stored;

                return stored;
            }
        }

        try
        {
            var fresh = Parse(filePath, length, lastWriteTicks, known);
            _cache[filePath] = fresh;
            SaveToDisk(filePath, fresh);

            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (known is null)
            {
                logger.LogError(ex, "Failed to read {File} and no previously cached rows exist; returning empty result", filePath);

                return null;
            }

            logger.LogWarning(ex, "Failed to read {File}; returning previously cached rows", filePath);

            return known;
        }
    }

    /// <summary>
    /// Parses a transcript, reusing the rows of an earlier parse when the file has only been appended to.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    /// <param name="length">The file's size observed before parsing.</param>
    /// <param name="lastWriteTicks">The file's last-write time observed before parsing.</param>
    /// <param name="known">The earlier state of the file, if any.</param>
    /// <returns>The new state of the file.</returns>
    private CachedSessionFile Parse(string filePath, long length, long lastWriteTicks, CachedSessionFile? known)
    {
        // The size and time were observed before parsing: if the file grows while it is read, the entry simply
        // looks stale next time and the new lines are then picked up incrementally.
        var canExtend = known is not null
                        && known.ParsedBytes <= length
                        && SessionFileFingerprint.Matches(filePath, known.ParsedBytes, known.Fingerprint);

        var startOffset = canExtend ? known!.ParsedBytes : 0;
        var parsed = sessionParser.ParseFrom(filePath, startOffset);

        IReadOnlyList<SessionMessage> rows = canExtend
            ? parsed.Rows.Count == 0 ? known!.Rows : [.. known!.Rows, .. parsed.Rows]
            : parsed.Rows;

        return new(length, lastWriteTicks, parsed.EndOffset, SessionFileFingerprint.Compute(filePath, parsed.EndOffset), rows);
    }

    /// <summary>
    /// Writes an entry to the disk cache, unless this process wrote the same file very recently. A file that is
    /// still being appended to changes on every refresh, and rewriting its (large) entry each time would cost
    /// more than it saves; a stale disk entry is harmless because the next process extends it incrementally.
    /// </summary>
    /// <param name="filePath">The transcript the entry describes.</param>
    /// <param name="entry">The entry to store.</param>
    private void SaveToDisk(string filePath, CachedSessionFile entry)
    {
        if (diskCache is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_lastDiskWriteTicks.TryGetValue(filePath, out var last) && now - last < DiskWriteInterval.TotalMilliseconds)
        {
            return;
        }

        _lastDiskWriteTicks[filePath] = now;
        diskCache.Save(filePath, entry);
    }
}
