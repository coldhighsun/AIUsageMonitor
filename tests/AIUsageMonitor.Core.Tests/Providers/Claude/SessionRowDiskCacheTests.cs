using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Providers.Claude.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for <see cref="SessionRowDiskCache"/> and its use by <see cref="SessionFileCache"/>, including incremental parsing.
/// </summary>
public sealed class SessionRowDiskCacheTests : IDisposable
{
    /// <summary>
    /// A fully populated assistant line (usage, cache-creation breakdown, tool use) with a non-UTC offset.
    /// </summary>
    private const string RichLine =
        """{"type":"assistant","timestamp":"2026-08-01T10:30:00.5+08:00","sessionId":"s1","cwd":"/proj","uuid":"u1","requestId":"r1","message":{"id":"m1","role":"assistant","model":"sonnet-5","content":[{"type":"tool_use"},{"type":"text"}],"usage":{"input_tokens":100,"output_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":7,"cache_creation":{"ephemeral_5m_input_tokens":3,"ephemeral_1h_input_tokens":4}}}}""";

    /// <summary>
    /// A minimal line with almost every optional field absent.
    /// </summary>
    private const string SparseLine = """{"type":"summary"}""";

    /// <summary>
    /// The parser shared by the tests.
    /// </summary>
    private readonly SessionParser _parser = new(NullLogger<SessionParser>.Instance);

    /// <summary>
    /// The disk cache under test, rooted in a per-test directory.
    /// </summary>
    private readonly SessionRowDiskCache _disk;

    /// <summary>
    /// The per-test directory holding both the cache entries and the transcript.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aimon-rows-{Guid.NewGuid()}");

    /// <summary>
    /// The transcript file used by the tests.
    /// </summary>
    private readonly string _transcript;

    /// <summary>
    /// Creates the per-test directory and the disk cache.
    /// </summary>
    public SessionRowDiskCacheTests()
    {
        Directory.CreateDirectory(_dir);
        _transcript = Path.Combine(_dir, "session.jsonl");
        _disk = new(Path.Combine(_dir, "cache"), NullLogger<SessionRowDiskCache>.Instance);
    }

    /// <summary>
    /// Removes the per-test directory.
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>
    /// Creates a file cache backed by the shared disk cache; each call is an independent "process" with an empty memory cache.
    /// </summary>
    /// <returns>A new <see cref="SessionFileCache"/>.</returns>
    private SessionFileCache NewProcess()
    {
        return new(_parser, NullLogger<SessionFileCache>.Instance, _disk);
    }

    /// <summary>
    /// Verifies that every field of a row survives a serialize/deserialize round trip.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_RowsWithAllFieldCombinations_RoundTripsEveryField()
    {
        File.WriteAllLines(_transcript, [RichLine, SparseLine, """{"type":"user","message":{"role":"user","content":"hi"}}"""]);
        var parsed = _parser.ParseFrom(_transcript, 0);
        var entry = new CachedSessionFile(123, 456, parsed.EndOffset, [1, 2, 3], parsed.Rows);

        var restored = SessionRowDiskCache.Deserialize(SessionRowDiskCache.Serialize(_transcript, entry), _transcript);

        Assert.NotNull(restored);
        Assert.Equal((123, 456, parsed.EndOffset), (restored.Length, restored.LastWriteUtcTicks, restored.ParsedBytes));
        Assert.Equal([1, 2, 3], restored.Fingerprint);
        Assert.Equal(3, restored.Rows.Count);

        var rich = restored.Rows[0];
        Assert.Equal("assistant", rich.Type);
        Assert.Equal("s1", rich.SessionId);
        Assert.Equal("/proj", rich.Cwd);
        Assert.Equal("u1", rich.Uuid);
        Assert.Equal("r1", rich.RequestId);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 30, 0, 500, TimeSpan.FromHours(8)), rich.Timestamp);
        Assert.Equal(TimeSpan.FromHours(8), rich.Timestamp!.Value.Offset);
        Assert.Equal("m1", rich.Message!.Id);
        Assert.Equal("assistant", rich.Message.Role);
        Assert.Equal("sonnet-5", rich.Message.Model);
        Assert.Equal(1, rich.Message.ToolUseCount);
        Assert.Equal((100, 50, 10, 7), (rich.Message.Usage!.InputTokens, rich.Message.Usage.OutputTokens,
            rich.Message.Usage.CacheReadInputTokens, rich.Message.Usage.CacheCreationInputTokens));
        Assert.Equal((3, 4), (rich.Message.Usage.CacheCreation!.Ephemeral5mInputTokens, rich.Message.Usage.CacheCreation.Ephemeral1hInputTokens));

        var sparse = restored.Rows[1];
        Assert.Equal("summary", sparse.Type);
        Assert.Null(sparse.SessionId);
        Assert.Null(sparse.Timestamp);
        Assert.Null(sparse.Message);

        Assert.Null(restored.Rows[2].Message!.Usage);
    }

    /// <summary>
    /// Verifies that an entry is refused when it was written for a different transcript path.
    /// </summary>
    [Fact]
    public void Deserialize_EntryForDifferentFile_ReturnsNull()
    {
        var entry = new CachedSessionFile(1, 2, 0, [], []);
        var bytes = SessionRowDiskCache.Serialize(_transcript, entry);

        Assert.Null(SessionRowDiskCache.Deserialize(bytes, Path.Combine(_dir, "other.jsonl")));
    }

    /// <summary>
    /// Verifies that a missing entry is a plain miss.
    /// </summary>
    [Fact]
    public void TryLoad_NoEntry_ReturnsNull()
    {
        Assert.Null(_disk.TryLoad(_transcript));
    }

    /// <summary>
    /// Verifies that truncated, garbage and wrong-version entries are all treated as misses instead of throwing.
    /// </summary>
    [Fact]
    public void TryLoad_CorruptOrForeignEntry_ReturnsNull()
    {
        File.WriteAllLines(_transcript, [RichLine, RichLine]);
        var parsed = _parser.ParseFrom(_transcript, 0);
        _disk.Save(_transcript, new(1, 2, parsed.EndOffset, [9], parsed.Rows));
        var entryPath = Directory.GetFiles(Path.Combine(_dir, "cache"), "*.rows").Single();
        var good = File.ReadAllBytes(entryPath);

        File.WriteAllBytes(entryPath, good[..(good.Length / 2)]);
        Assert.Null(_disk.TryLoad(_transcript));

        File.WriteAllBytes(entryPath, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        Assert.Null(_disk.TryLoad(_transcript));

        var wrongVersion = (byte[])good.Clone();
        wrongVersion[4]++;
        File.WriteAllBytes(entryPath, wrongVersion);
        Assert.Null(_disk.TryLoad(_transcript));

        File.WriteAllBytes(entryPath, good);
        Assert.NotNull(_disk.TryLoad(_transcript));
    }

    /// <summary>
    /// Verifies that a second process serves an unchanged transcript from the disk cache without parsing it.
    /// </summary>
    [Fact]
    public void GetRows_UnchangedFileInNewProcess_IsServedFromDiskCache()
    {
        File.WriteAllLines(_transcript, [RichLine, SparseLine]);
        var first = NewProcess().GetRows(_transcript);

        // Same size and time, but different content: only a cache hit can still return the original rows.
        var info = new FileInfo(_transcript);
        var mtime = info.LastWriteTimeUtc;
        var tampered = File.ReadAllText(_transcript).Replace("sonnet-5", "sonnet-9");
        File.WriteAllText(_transcript, tampered);
        File.SetLastWriteTimeUtc(_transcript, mtime);

        var second = NewProcess().GetRows(_transcript);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal("sonnet-5", second[0].Message!.Model);
    }

    /// <summary>
    /// Verifies that a file that was only appended to is extended with just the new lines.
    /// </summary>
    [Fact]
    public void GetRows_FileAppendedSinceDiskEntry_ReturnsOldPlusNewRows()
    {
        File.WriteAllLines(_transcript, [RichLine, SparseLine]);
        NewProcess().GetRows(_transcript);

        File.AppendAllLines(_transcript, ["""{"type":"user","sessionId":"s2"}"""]);
        File.SetLastWriteTimeUtc(_transcript, DateTime.UtcNow.AddSeconds(5));
        var rows = NewProcess().GetRows(_transcript);

        Assert.Equal(["assistant", "summary", "user"], rows.Select(r => r.Type));
        Assert.Equal("s2", rows[2].SessionId);
    }

    /// <summary>
    /// Verifies that the incrementally built result equals a from-scratch parse of the same file.
    /// </summary>
    [Fact]
    public void GetRows_AppendedInMemory_MatchesFullParse()
    {
        var sut = NewProcess();
        File.WriteAllLines(_transcript, [RichLine]);
        sut.GetRows(_transcript);

        File.AppendAllLines(_transcript, [SparseLine, RichLine]);
        File.SetLastWriteTimeUtc(_transcript, DateTime.UtcNow.AddSeconds(5));
        var incremental = sut.GetRows(_transcript);

        var full = _parser.ParseFile(_transcript).ToList();
        Assert.Equal(full.Select(r => (r.Type, r.Timestamp, r.Message?.Usage?.InputTokens)),
            incremental.Select(r => (r.Type, r.Timestamp, r.Message?.Usage?.InputTokens)));
    }

    /// <summary>
    /// Verifies that a rewritten (not appended) file is fully re-parsed even though it grew, because its prefix changed.
    /// </summary>
    [Fact]
    public void GetRows_FileRewrittenWithDifferentPrefix_IsParsedFromScratch()
    {
        File.WriteAllLines(_transcript, [RichLine]);
        var sut = NewProcess();
        sut.GetRows(_transcript);

        File.WriteAllLines(_transcript, ["""{"type":"user","sessionId":"new"}""", SparseLine, RichLine, RichLine]);
        File.SetLastWriteTimeUtc(_transcript, DateTime.UtcNow.AddSeconds(5));
        var rows = sut.GetRows(_transcript);

        Assert.Equal(["user", "summary", "assistant", "assistant"], rows.Select(r => r.Type));
    }

    /// <summary>
    /// Verifies that a file that shrank is fully re-parsed.
    /// </summary>
    [Fact]
    public void GetRows_FileShrank_IsParsedFromScratch()
    {
        File.WriteAllLines(_transcript, [RichLine, RichLine, RichLine]);
        var sut = NewProcess();
        sut.GetRows(_transcript);

        File.WriteAllLines(_transcript, [SparseLine]);
        File.SetLastWriteTimeUtc(_transcript, DateTime.UtcNow.AddSeconds(5));
        var rows = sut.GetRows(_transcript);

        Assert.Equal(["summary"], rows.Select(r => r.Type));
    }

    /// <summary>
    /// Verifies that a half-written trailing line is not consumed and is picked up once it is complete.
    /// </summary>
    [Fact]
    public void GetRows_PartialTrailingLine_IsCompletedOnNextRefresh()
    {
        var sut = NewProcess();
        File.WriteAllText(_transcript, SparseLine + "\n" + """{"type":"user","sess""");
        Assert.Single(sut.GetRows(_transcript));

        File.AppendAllText(_transcript, """ionId":"s9"}""" + "\n");
        File.SetLastWriteTimeUtc(_transcript, DateTime.UtcNow.AddSeconds(5));
        var rows = sut.GetRows(_transcript);

        Assert.Equal(2, rows.Count);
        Assert.Equal("s9", rows[1].SessionId);
    }

    /// <summary>
    /// Verifies that pruning deletes entries of vanished transcripts, keeps live ones, and never wipes everything on an empty list.
    /// </summary>
    [Fact]
    public void Prune_RemovesEntriesOfMissingFilesOnly()
    {
        var other = Path.Combine(_dir, "other.jsonl");
        File.WriteAllLines(_transcript, [SparseLine]);
        File.WriteAllLines(other, [SparseLine]);
        var sut = NewProcess();
        sut.GetRows(_transcript);
        sut.GetRows(other);
        var cacheDir = Path.Combine(_dir, "cache");

        _disk.Prune([]);
        Assert.Equal(2, Directory.GetFiles(cacheDir, "*.rows").Length);

        _disk.Prune([_transcript]);
        Assert.Single(Directory.GetFiles(cacheDir, "*.rows"));
        Assert.NotNull(_disk.TryLoad(_transcript));
        Assert.Null(_disk.TryLoad(other));
    }

    /// <summary>
    /// Verifies that removing a file from the cache also drops its disk entry.
    /// </summary>
    [Fact]
    public void Remove_DropsDiskEntry()
    {
        File.WriteAllLines(_transcript, [SparseLine]);
        var sut = NewProcess();
        sut.GetRows(_transcript);

        sut.Remove(_transcript);

        Assert.Null(_disk.TryLoad(_transcript));
    }

    /// <summary>
    /// Verifies that an unwritable cache location only disables persistence and never breaks reading.
    /// </summary>
    [Fact]
    public void GetRows_CacheDirectoryUnusable_StillReturnsRows()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where the cache directory should be");
        var broken = new SessionRowDiskCache(Path.Combine(blocker, "cache"), NullLogger<SessionRowDiskCache>.Instance);
        File.WriteAllLines(_transcript, [SparseLine]);

        var rows = new SessionFileCache(_parser, NullLogger<SessionFileCache>.Instance, broken).GetRows(_transcript);

        Assert.Single(rows);
    }

    /// <summary>
    /// Verifies the byte offsets reported by the parser: the end of the last newline-terminated line, so that a
    /// later call starting there yields exactly the lines appended afterwards.
    /// </summary>
    [Fact]
    public void ParseFrom_ResumingAtEndOffset_YieldsOnlyAppendedLines()
    {
        File.WriteAllLines(_transcript, [SparseLine, SparseLine]);
        var first = _parser.ParseFrom(_transcript, 0);
        var lengthBeforeAppend = new FileInfo(_transcript).Length;

        File.AppendAllLines(_transcript, ["""{"type":"user"}"""]);
        var second = _parser.ParseFrom(_transcript, first.EndOffset);

        Assert.Equal(2, first.Rows.Count);
        Assert.Equal(lengthBeforeAppend, first.EndOffset);
        Assert.Equal(["user"], second.Rows.Select(r => r.Type));
        Assert.Equal(new FileInfo(_transcript).Length, second.EndOffset);
    }

    /// <summary>
    /// Verifies that a line longer than the initial read buffer, a UTF-8 byte-order mark and CRLF line endings are all handled.
    /// </summary>
    [Fact]
    public void ParseFrom_LongLineBomAndCrLf_ParsesAllRows()
    {
        var longText = new string('x', 700_000);
        var content = $"{{\"type\":\"user\",\"cwd\":\"{longText}\"}}\r\n{SparseLine}\r\n";
        File.WriteAllBytes(_transcript, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(content)]);

        var result = _parser.ParseFrom(_transcript, 0);

        Assert.Equal(["user", "summary"], result.Rows.Select(r => r.Type));
        Assert.Equal(longText.Length, result.Rows[0].Cwd!.Length);
        Assert.Equal(new FileInfo(_transcript).Length, result.EndOffset);
    }

    /// <summary>
    /// Verifies that a complete final line without a trailing newline is still consumed.
    /// </summary>
    [Fact]
    public void ParseFrom_CompleteLastLineWithoutNewline_IsConsumed()
    {
        File.WriteAllText(_transcript, SparseLine + "\n" + """{"type":"user"}""");

        var result = _parser.ParseFrom(_transcript, 0);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new FileInfo(_transcript).Length, result.EndOffset);
    }
}
