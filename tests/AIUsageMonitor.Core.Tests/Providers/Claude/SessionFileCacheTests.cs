using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class SessionFileCacheTests : IDisposable
{
    private readonly SessionFileCache _sut = new(
        new SessionParser(NullLogger<SessionParser>.Instance),
        NullLogger<SessionFileCache>.Instance);

    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void GetRows_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(_sut.GetRows(_tempFile));
    }

    [Fact]
    public void GetRows_ParsesAndCachesFile()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);

        var rows = _sut.GetRows(_tempFile);

        Assert.Single(rows);
    }

    [Fact]
    public void GetRows_FileUnchanged_ReturnsCachedInstanceWithoutReparsing()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);

        var first = _sut.GetRows(_tempFile);
        var second = _sut.GetRows(_tempFile);

        Assert.Same(first, second);
    }

    [Fact]
    public void GetRows_FileModified_Reparses()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        _sut.GetRows(_tempFile);

        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
            """{"type":"assistant","timestamp":"2026-08-01T00:01:00Z","sessionId":"s1"}""",
        ]);
        File.SetLastWriteTimeUtc(_tempFile, DateTime.UtcNow.AddSeconds(5));

        var rows = _sut.GetRows(_tempFile);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void GetRows_FileDeletedAfterCaching_RemovesEntryAndReturnsEmpty()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        _sut.GetRows(_tempFile);

        File.Delete(_tempFile);

        Assert.Empty(_sut.GetRows(_tempFile));
    }

    [Fact]
    public void Remove_ClearsCachedEntry_SoFileReparsesEvenIfWriteTimeUnchanged()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        var first = _sut.GetRows(_tempFile);

        _sut.Remove(_tempFile);
        var second = _sut.GetRows(_tempFile);

        Assert.NotSame(first, second);
        Assert.Single(second);
    }

    [Fact]
    public void Prune_RemovesEntriesNotInCurrentFiles()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        var first = _sut.GetRows(_tempFile);

        _sut.Prune([]);
        var second = _sut.GetRows(_tempFile);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Prune_KeepsEntriesStillInCurrentFiles()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        var first = _sut.GetRows(_tempFile);

        _sut.Prune([_tempFile]);
        var second = _sut.GetRows(_tempFile);

        Assert.Same(first, second);
    }

    [Fact]
    public void Set_MissingFile_RemovesCachedEntry()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);
        var first = _sut.GetRows(_tempFile);
        File.Delete(_tempFile);

        _sut.Set(_tempFile);

        Assert.Empty(_sut.GetRows(_tempFile));
        Assert.NotEmpty(first);
    }

    [Fact]
    public void Set_NewFile_PopulatesCacheSoSubsequentGetRowsReturnsSameInstance()
    {
        File.WriteAllLines(_tempFile,
        [
            """{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}""",
        ]);

        _sut.Set(_tempFile);
        var first = _sut.GetRows(_tempFile);
        var second = _sut.GetRows(_tempFile);

        Assert.Same(first, second);
        Assert.Single(first);
    }

    [Fact]
    public void GetFilesModifiedSince_FiltersOutFilesOlderThanWindow()
    {
        var recent = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");
        var stale = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");
        var missing = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");
        try
        {
            File.WriteAllText(recent, "");
            File.WriteAllText(stale, "");
            File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10));

            var result = _sut.GetFilesModifiedSince([recent, stale, missing], DateTimeOffset.UtcNow.AddDays(-1));

            Assert.Equal([recent], result);
        }
        finally
        {
            File.Delete(recent);
            File.Delete(stale);
        }
    }

    [Fact]
    public void GetFilesModifiedSince_FileJustBeforeWindowStart_IsKeptForClockSkew()
    {
        File.WriteAllText(_tempFile, "");
        File.SetLastWriteTimeUtc(_tempFile, DateTime.UtcNow.AddDays(-1).AddMinutes(-10));

        var result = _sut.GetFilesModifiedSince([_tempFile], DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal([_tempFile], result);
    }

    [Fact]
    public void WarmUp_ParsesFilesInParallelAndReportsCompletion()
    {
        var files = Enumerable.Range(0, 20).Select(i => Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}-{i}.jsonl")).ToList();
        var missing = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");
        var reported = new List<int>();
        var progress = new SynchronousProgress(reported.Add);
        try
        {
            foreach (var file in files)
            {
                File.WriteAllLines(file, ["""{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}"""]);
            }

            _sut.WarmUp([.. files, missing], progress);

            Assert.Equal(21, reported.Count);
            Assert.Equal(100, reported.Max());
            Assert.All(files, f => Assert.Same(_sut.GetRows(f), _sut.GetRows(f)));
            Assert.All(files, f => Assert.Single(_sut.GetRows(f)));
        }
        finally
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// Verifies that progress reports from the parallel warm-up arrive one at a time and in increasing order,
    /// so a consumer that is not thread-safe (and a progress bar) sees a monotonic sequence ending at 100.
    /// </summary>
    [Fact]
    public void WarmUp_ManyFiles_ReportsAreSerializedAndMonotonic()
    {
        var files = Enumerable.Range(0, 200).Select(i => Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}-{i}.jsonl")).ToList();
        var reported = new List<int>();
        try
        {
            foreach (var file in files)
            {
                File.WriteAllLines(file, ["""{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s1"}"""]);
            }

            // Deliberately not thread-safe: the warm-up itself must serialize its reports.
            _sut.WarmUp(files, new InlineProgress(reported.Add));

            Assert.Equal(200, reported.Count);
            Assert.Equal(reported.Order(), reported);
            Assert.Equal(100, reported[^1]);
        }
        finally
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// A progress reporter that invokes its callback inline and does no synchronization of its own.
    /// </summary>
    /// <param name="report">The callback invoked for each report.</param>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        /// <summary>
        /// Forwards the value to the callback on the calling thread.
        /// </summary>
        /// <param name="value">The reported value.</param>
        public void Report(int value) => report(value);
    }

    /// <summary>
    /// Verifies that threads refreshing the same changed file at once share one parse: every caller gets the same instance.
    /// </summary>
    [Fact]
    public void GetRows_ConcurrentCallersForSameChangedFile_ShareOneParsedResult()
    {
        File.WriteAllLines(_tempFile, Enumerable.Range(0, 5000)
            .Select(i => $$"""{"type":"user","timestamp":"2026-08-01T00:00:00Z","sessionId":"s{{i}}"}"""));

        var results = Enumerable.Range(0, 16).AsParallel().WithDegreeOfParallelism(16)
            .Select(_ => _sut.GetRows(_tempFile))
            .ToList();

        Assert.All(results, r => Assert.Same(results[0], r));
        Assert.Equal(5000, results[0].Count);
    }

    /// <summary>
    /// A progress reporter that invokes its callback inline (unlike <see cref="Progress{T}"/>, which posts to the thread pool),
    /// serialized so concurrent reports from the parallel warm-up can safely share a plain list.
    /// </summary>
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        private readonly object _gate = new();

        public void Report(int value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
