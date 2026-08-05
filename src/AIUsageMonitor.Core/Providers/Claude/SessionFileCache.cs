using AIUsageMonitor.Core.Providers.Claude.Models;
using System.Collections.Concurrent;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Caches each session transcript's parsed rows keyed by file path + last-write time, so
/// re-aggregating stats after a cache invalidation only reparses files that actually changed.
/// </summary>
/// <param name="sessionParser">The <see cref="SessionParser"/> used to parse session transcript files.</param>
public sealed class SessionFileCache(SessionParser sessionParser)
{
    /// <summary>
    /// A thread-safe dictionary that caches parsed session transcript rows, keyed by file path and last-write time.
    /// </summary>
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, IReadOnlyList<SessionMessage> Rows)> _cache = new();

    /// <summary>
    /// Gets the parsed rows for a given session transcript file. If the file has not changed since the last read, returns the cached rows; otherwise, reparses the file and updates the cache.
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

        var rows = sessionParser.ParseFile(filePath).ToList();
        _cache[filePath] = (lastWriteUtc, rows);

        return rows;
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
    /// Adds or updates the cached entry for a specific session transcript file.
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

        var rows = sessionParser.ParseFile(filePath).ToList();
        _cache[filePath] = (lastWriteUtc, rows);
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
}