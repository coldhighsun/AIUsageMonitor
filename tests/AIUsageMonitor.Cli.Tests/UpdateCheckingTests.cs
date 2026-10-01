using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="UpdateCheckCache"/> and the cache-first flow of <see cref="UpdateChecking"/>.
/// </summary>
public sealed class UpdateCheckingTests : IDisposable
{
    /// <summary>
    /// The running version used by the tests.
    /// </summary>
    private const string Version = "1.2.3";

    /// <summary>
    /// The directory holding the per-test cache file.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aimon-update-{Guid.NewGuid()}");

    /// <summary>
    /// The controllable clock the cache uses.
    /// </summary>
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The cache under test.
    /// </summary>
    private readonly UpdateCheckCache _cache;

    /// <summary>
    /// Creates the cache over a file in a fresh, not yet existing directory.
    /// </summary>
    public UpdateCheckingTests()
    {
        _cache = new(Path.Combine(_dir, "update-check.json"), _clock);
    }

    /// <summary>
    /// Removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A clock whose current time is set explicitly.
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
    /// Verifies that an empty cache yields nothing.
    /// </summary>
    [Fact]
    public void TryGet_NoFile_ReturnsNull()
    {
        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that a saved outcome round-trips while still fresh.
    /// </summary>
    [Fact]
    public void TryGet_FreshSuccessfulEntry_ReturnsSavedOutcome()
    {
        _cache.Save(Version, new UpdateCheckOutcome(true, "2.0.0", UpdateChecking.ReleaseUrl), succeeded: true);
        _clock.Now += TimeSpan.FromHours(23);

        var result = _cache.TryGet(Version);

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.0", result.LatestVersion);
    }

    /// <summary>
    /// Verifies that a successful entry expires after its lifetime.
    /// </summary>
    [Fact]
    public void TryGet_SuccessfulEntryOlderThanLifetime_ReturnsNull()
    {
        _cache.Save(Version, new UpdateCheckOutcome(false, null, UpdateChecking.ReleaseUrl), succeeded: true);
        _clock.Now += UpdateCheckCache.SuccessLifetime;

        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that a failed check is retried sooner than a successful one.
    /// </summary>
    [Fact]
    public void TryGet_FailedEntryOlderThanFailureLifetime_ReturnsNull()
    {
        _cache.Save(Version, new UpdateCheckOutcome(false, null, UpdateChecking.ReleaseUrl), succeeded: false);

        _clock.Now += TimeSpan.FromMinutes(30);
        Assert.NotNull(_cache.TryGet(Version));

        _clock.Now += UpdateCheckCache.FailureLifetime;
        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that an entry written by another version is ignored, so an upgrade rechecks immediately.
    /// </summary>
    [Fact]
    public void TryGet_EntryForDifferentVersion_ReturnsNull()
    {
        _cache.Save("1.0.0", new UpdateCheckOutcome(true, "1.2.3", UpdateChecking.ReleaseUrl), succeeded: true);

        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that an entry stamped in the future (clock moved back) is not trusted.
    /// </summary>
    [Fact]
    public void TryGet_EntryFromTheFuture_ReturnsNull()
    {
        _cache.Save(Version, new UpdateCheckOutcome(false, null, UpdateChecking.ReleaseUrl), succeeded: true);
        _clock.Now -= TimeSpan.FromHours(2);

        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that a corrupt cache file is treated as a miss rather than an error.
    /// </summary>
    [Fact]
    public void TryGet_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "update-check.json"), "{ not json");

        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that a fresh cache entry avoids any network fetch.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_FreshCache_DoesNotFetch()
    {
        _cache.Save(Version, new UpdateCheckOutcome(true, "2.0.0", UpdateChecking.ReleaseUrl), succeeded: true);
        var fetches = 0;

        var result = await UpdateChecking.CheckForUpdateAsync(Version, _cache, (_, _) =>
        {
            fetches++;
            return Task.FromResult<UpdateCheckOutcome?>(null);
        }, CancellationToken.None);

        Assert.Equal(0, fetches);
        Assert.True(result.IsUpdateAvailable);
    }

    /// <summary>
    /// Verifies that a miss fetches once and the result is then served from the cache.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_Miss_FetchesOnceAndCachesResult()
    {
        var fetches = 0;
        Task<UpdateCheckOutcome?> Fetch(string version, CancellationToken ct)
        {
            fetches++;
            return Task.FromResult<UpdateCheckOutcome?>(new UpdateCheckOutcome(true, "2.0.0", UpdateChecking.ReleaseUrl));
        }

        var first = await UpdateChecking.CheckForUpdateAsync(Version, _cache, Fetch, CancellationToken.None);
        var second = await UpdateChecking.CheckForUpdateAsync(Version, _cache, Fetch, CancellationToken.None);

        Assert.Equal(1, fetches);
        Assert.True(first.IsUpdateAvailable);
        Assert.True(second.IsUpdateAvailable);
    }

    /// <summary>
    /// Verifies that a failed fetch reports no update and is remembered briefly instead of being retried at once.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_FetchFails_ReportsNoUpdateAndBacksOff()
    {
        var fetches = 0;
        Task<UpdateCheckOutcome?> Fetch(string version, CancellationToken ct)
        {
            fetches++;
            return Task.FromResult<UpdateCheckOutcome?>(null);
        }

        var first = await UpdateChecking.CheckForUpdateAsync(Version, _cache, Fetch, CancellationToken.None);
        var second = await UpdateChecking.CheckForUpdateAsync(Version, _cache, Fetch, CancellationToken.None);

        Assert.False(first.IsUpdateAvailable);
        Assert.False(second.IsUpdateAvailable);
        Assert.Equal(1, fetches);
    }

    /// <summary>
    /// Verifies that a cancelled check is not remembered, so the next run checks for real.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_Cancelled_ReportsNoUpdateAndDoesNotCache()
    {
        var result = await UpdateChecking.CheckForUpdateAsync(
            Version, _cache, (_, _) => throw new OperationCanceledException(), CancellationToken.None);

        Assert.False(result.IsUpdateAvailable);
        Assert.Null(_cache.TryGet(Version));
    }

    /// <summary>
    /// Verifies that an unknown running version skips the check entirely.
    /// </summary>
    [Fact]
    public async Task CheckForUpdateAsync_UnknownVersion_DoesNotFetch()
    {
        var fetches = 0;

        var result = await UpdateChecking.CheckForUpdateAsync(null, _cache, (_, _) =>
        {
            fetches++;
            return Task.FromResult<UpdateCheckOutcome?>(null);
        }, CancellationToken.None);

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(0, fetches);
    }

    /// <summary>
    /// Verifies that the notice waits only up to the limit for a check that never finishes.
    /// </summary>
    [Fact]
    public async Task PrintIfAvailableAsync_CheckStillRunning_ReturnsAfterMaxWait()
    {
        var never = new TaskCompletionSource<UpdateCheckOutcome>().Task;
        var started = DateTimeOffset.UtcNow;

        await UpdateNotice.PrintIfAvailableAsync(never, TimeSpan.FromMilliseconds(100));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Verifies that a check that threw does not fail the command.
    /// </summary>
    [Fact]
    public async Task PrintIfAvailableAsync_CheckFaulted_DoesNotThrow()
    {
        var faulted = Task.FromException<UpdateCheckOutcome>(new InvalidOperationException("boom"));

        await UpdateNotice.PrintIfAvailableAsync(faulted, TimeSpan.FromSeconds(1));
    }
}
