using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for the remembered last-write times behind <see cref="SessionFileCache.GetFilesModifiedSince"/>.
/// </summary>
public sealed class SessionFileCacheWriteTimeTests : IDisposable
{
    /// <summary>
    /// The window start used by the tests: one day before the fixed clock.
    /// </summary>
    private static readonly DateTimeOffset Since = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The controllable clock the cache uses.
    /// </summary>
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The cache under test.
    /// </summary>
    private readonly SessionFileCache _sut;

    /// <summary>
    /// A transcript whose last-write time is days before the window.
    /// </summary>
    private readonly string _idleFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    /// <summary>
    /// Creates the cache with a controllable clock and the idle transcript.
    /// </summary>
    public SessionFileCacheWriteTimeTests()
    {
        _sut = new(
            new SessionParser(NullLogger<SessionParser>.Instance),
            NullLogger<SessionFileCache>.Instance,
            diskCache: null,
            timeProvider: _clock);

        File.WriteAllText(_idleFile, "");
        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddDays(-5));
    }

    /// <summary>
    /// Removes the transcript.
    /// </summary>
    public void Dispose()
    {
        File.Delete(_idleFile);
    }

    /// <summary>
    /// A clock whose time is set explicitly.
    /// </summary>
    /// <param name="now">The initial time.</param>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>
        /// Gets or sets the current time.
        /// </summary>
        public DateTimeOffset Now { get; set; } = now;

        /// <summary>
        /// Returns the configured time.
        /// </summary>
        /// <returns>The configured time.</returns>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Verifies that without tracking every call reads the disk, so an unreported write is seen at once.
    /// </summary>
    [Fact]
    public void GetFilesModifiedSince_TrackingOff_ReadsDiskEveryCall()
    {
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));

        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddHours(1));

        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that with tracking an unreported write is not seen, which is what saves the per-file disk reads.
    /// </summary>
    [Fact]
    public void GetFilesModifiedSince_TrackingOn_UsesRememberedTimeInsteadOfReadingDisk()
    {
        _sut.EnableWriteTimeTracking();
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));

        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddHours(1));

        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that a reported write makes a previously idle file visible again.
    /// </summary>
    [Fact]
    public void NoteWritten_IdleFile_IsIncludedAfterwards()
    {
        _sut.EnableWriteTimeTracking();
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));

        _sut.NoteWritten(_idleFile, Since.UtcDateTime.AddHours(1));

        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that a stale report (older than what is remembered) never hides a file that was already seen as recent.
    /// </summary>
    [Fact]
    public void NoteWritten_OlderTimeThanRemembered_DoesNotMoveTimeBackwards()
    {
        _sut.EnableWriteTimeTracking();
        _sut.NoteWritten(_idleFile, Since.UtcDateTime.AddHours(1));

        _sut.NoteWritten(_idleFile, Since.UtcDateTime.AddDays(-5));

        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that reports are ignored while tracking is off, so nothing stale is remembered later.
    /// </summary>
    [Fact]
    public void NoteWritten_TrackingOff_IsIgnored()
    {
        _sut.NoteWritten(_idleFile, Since.UtcDateTime.AddHours(1));

        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that forgetting write times (lost notifications) makes the next call read the disk.
    /// </summary>
    [Fact]
    public void ForgetWriteTimes_NextCallReadsDisk()
    {
        _sut.EnableWriteTimeTracking();
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddHours(1));

        _sut.ForgetWriteTimes();

        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies the safety net: remembered times older than the maximum age are re-read from disk even with no events.
    /// </summary>
    [Fact]
    public void GetFilesModifiedSince_RememberedTimesOlderThanMaxAge_AreReRead()
    {
        _sut.EnableWriteTimeTracking();
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddHours(1));

        _clock.Now += SessionFileCache.WriteTimeMaxAge - TimeSpan.FromSeconds(1);
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));

        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that removing a file and pruning drop remembered times, so a recreated or unlisted file is read afresh.
    /// </summary>
    [Fact]
    public void RemoveAndPrune_DropRememberedTimes()
    {
        _sut.EnableWriteTimeTracking();
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddHours(1));

        _sut.Remove(_idleFile);
        Assert.Equal([_idleFile], _sut.GetFilesModifiedSince([_idleFile], Since));

        File.SetLastWriteTimeUtc(_idleFile, Since.UtcDateTime.AddDays(-5));
        _sut.Prune([Path.Combine(Path.GetTempPath(), "other.jsonl")]);
        Assert.Empty(_sut.GetFilesModifiedSince([_idleFile], Since));
    }

    /// <summary>
    /// Verifies that a missing file is skipped, and picked up once it is reported as written.
    /// </summary>
    [Fact]
    public void GetFilesModifiedSince_MissingFileLaterCreated_IsIncludedOnceReported()
    {
        _sut.EnableWriteTimeTracking();
        var path = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");
        try
        {
            Assert.Empty(_sut.GetFilesModifiedSince([path], Since));

            File.WriteAllText(path, "");
            File.SetLastWriteTimeUtc(path, Since.UtcDateTime.AddHours(1));
            _sut.NoteWritten(path, File.GetLastWriteTimeUtc(path));

            Assert.Equal([path], _sut.GetFilesModifiedSince([path], Since));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
