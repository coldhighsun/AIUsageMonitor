using System.Collections.Concurrent;
using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Caches each session transcript's parsed rows keyed by file path + last-write time, so
/// re-aggregating stats after a cache invalidation only re-parses files that actually changed.
/// </summary>
public sealed class SessionFileCache(SessionParser sessionParser)
{
    private readonly ConcurrentDictionary<string, (DateTime LastWriteUtc, List<SessionMessage> Rows)> _cache = new();

    public List<SessionMessage> GetRows(string filePath)
    {
        var lastWriteUtc = File.GetLastWriteTimeUtc(filePath);
        if (_cache.TryGetValue(filePath, out var entry) && entry.LastWriteUtc == lastWriteUtc)
        {
            return entry.Rows;
        }

        var rows = sessionParser.ParseFile(filePath).ToList();
        _cache[filePath] = (lastWriteUtc, rows);
        return rows;
    }

    public void Prune(IReadOnlyCollection<string> liveFiles)
    {
        var liveSet = liveFiles.ToHashSet();
        foreach (var key in _cache.Keys)
        {
            if (!liveSet.Contains(key))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }
}
