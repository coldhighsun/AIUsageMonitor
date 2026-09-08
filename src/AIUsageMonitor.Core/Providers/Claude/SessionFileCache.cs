using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Caches each session transcript's parsed rows keyed by file path + last-write time, so
/// re-aggregating stats after a cache invalidation only reparses files that actually changed.
/// </summary>
/// <param name="sessionParser">The <see cref="SessionParser"/> used to parse session transcript files.</param>
/// <param name="logger">The logger instance used for logging warnings when a transcript file cannot be read.</param>
public sealed class SessionFileCache(SessionParser sessionParser, ILogger<SessionFileCache> logger)
{
    /// <summary>
    /// A thread-safe dictionary that caches parsed session transcript rows, keyed by file path and last-write time.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, IReadOnlyList<SessionMessage> Rows)> _cache = new();

    /// <summary>
    /// Gets the parsed rows for a given session transcript file. If the file has not changed since the last read, returns the cached rows; otherwise, reparses the file and updates the cache.
    /// If the file cannot be read (e.g. it is temporarily locked by another process), the previously cached rows are returned instead, if any.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    /// <returns>A list of <see cref="SessionMessage"/> objects representing the parsed rows of the session transcript.</returns>
    public IReadOnlyList<SessionMessage> GetRows(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Remove(filePath);

            return [];
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        if (_cache.TryGetValue(filePath, out var entry) && entry.LastWriteUtc == lastWriteUtc)
        {
            return entry.Rows;
        }

        try
        {
            var rows = sessionParser.ParseFile(filePath).ToList();
            _cache[filePath] = (lastWriteUtc, rows);

            return rows;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (entry.Rows is null)
            {
                logger.LogError(ex, "Failed to read {File} and no previously cached rows exist; returning empty result", filePath);

                return [];
            }

            logger.LogWarning(ex, "Failed to read {File}; returning previously cached rows", filePath);

            return entry.Rows;
        }
    }

    /// <summary>
    /// Removes cached entries for files that are no longer present in <paramref name="currentFiles"/>.
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
            }
        }
    }

    /// <summary>
    /// Removes the cached entry for a specific session transcript file, if it exists. This can be used to manually invalidate the cache for a particular file.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    public void Remove(string filePath)
    {
        _cache.TryRemove(filePath, out _);
    }

    /// <summary>
    /// Adds or updates the cached entry for a specific session transcript file. If the file cannot be read
    /// (e.g. it is temporarily locked by another process), the previously cached entry is left untouched so
    /// that no stats are lost; the file will be retried on the next change notification.
    /// </summary>
    /// <param name="filePath">The path to the session transcript file.</param>
    public void Set(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Remove(filePath);

            return;
        }

        var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        if (_cache.TryGetValue(filePath, out var entry) && entry.LastWriteUtc == lastWriteUtc)
        {
            return;
        }

        try
        {
            var rows = sessionParser.ParseFile(filePath).ToList();
            _cache[filePath] = (lastWriteUtc, rows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (entry.Rows is null)
            {
                logger.LogError(ex, "Failed to read {File} for the first time; no cache entry was created", filePath);
            }
            else
            {
                logger.LogWarning(ex, "Failed to refresh cache for {File}; keeping previously cached rows", filePath);
            }
        }
    }
}