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
}
