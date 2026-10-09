using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for how usage windows and hourly buckets are placed in time: around a known reset, near the end of an
/// estimated window, across daylight-saving changes and in time zones with a half-hour offset.
/// </summary>
public class WindowPlacementTests : IDisposable
{
    /// <summary>
    /// The transcript file written by a test.
    /// </summary>
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"session-{Guid.NewGuid()}.jsonl");

    /// <summary>
    /// The cache the builders read the transcript through.
    /// </summary>
    private readonly SessionFileCache _cache = new(
        new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance);

    /// <summary>
    /// Deletes the transcript file.
    /// </summary>
    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    /// <summary>
    /// Builds an assistant transcript line.
    /// </summary>
    /// <param name="timestamp">The line's timestamp.</param>
    /// <param name="id">A unique id for the message.</param>
    /// <returns>The JSON line.</returns>
    private static string Line(DateTimeOffset timestamp, string id)
    {
        return "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp.ToString("O") + "\",\"sessionId\":\"s1\",\"requestId\":\"r-"
            + id + "\",\"message\":{\"id\":\"m-" + id + "\",\"role\":\"assistant\",\"model\":\"sonnet-5\",\"usage\":{\"input_tokens\":100,"
            + "\"output_tokens\":0,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}";
    }

    /// <summary>
    /// Writes the transcript and gives it the clock's current time as last-write time, so the file-age filter of the
    /// builders sees it as written "now" even though the test clock is fixed in the past.
    /// </summary>
    /// <param name="clock">The clock the builder under test uses.</param>
    /// <param name="lines">The transcript lines.</param>
    private void WriteTranscript(FixedClock clock, params string[] lines)
    {
        File.WriteAllLines(_tempFile, lines);
        File.SetLastWriteTimeUtc(_tempFile, clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Verifies that activity just before a known (elapsed) reset is not counted in the next window, even when the
    /// estimated window start is floored to a time before the reset.
    /// </summary>
    [Fact]
    public void BuildCurrentSessionWindow_ElapsedResetInsideFlooredStart_ExcludesActivityBeforeTheReset()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));
        var reset = new DateTimeOffset(2026, 3, 10, 11, 35, 0, TimeSpan.Zero);
        WriteTranscript(
            clock,
            Line(new DateTimeOffset(2026, 3, 10, 11, 33, 0, TimeSpan.Zero), "before"),
            Line(new DateTimeOffset(2026, 3, 10, 11, 36, 0, TimeSpan.Zero), "after"));
        var sut = new SessionBlockBuilder(_cache, new CostCalculator(), clock);

        var window = sut.BuildCurrentSessionWindow([_tempFile], reset);

        Assert.Equal(WindowConfidence.Estimated, window.Confidence);
        Assert.Equal(1, window.Messages);
        Assert.Equal(100, window.TotalTokens);
    }

    /// <summary>
    /// Verifies that a window whose displayed (floored) reset has passed is reported as unknown instead of showing a reset in the past.
    /// </summary>
    [Fact]
    public void BuildCurrentSessionWindow_FlooredResetAlreadyPassed_ReportsUnknown()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 3, 10, 16, 32, 0, TimeSpan.Zero));
        WriteTranscript(clock, Line(new DateTimeOffset(2026, 3, 10, 11, 39, 0, TimeSpan.Zero), "first"));
        var sut = new SessionBlockBuilder(_cache, new CostCalculator(), clock);

        var window = sut.BuildCurrentSessionWindow([_tempFile], sessionResetAt: null);

        Assert.Equal(WindowConfidence.Unknown, window.Confidence);
        Assert.Null(window.ResetsAt);
    }

    /// <summary>
    /// Verifies that a weekly window that spans the start of daylight saving time keeps its start and reset on the anchor's wall-clock time.
    /// </summary>
    [Fact]
    public void GetAnchoredWeek_DaylightSavingStartsInsideTheWeek_KeepsWallClockTimeAtBothEnds()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var now = new DateTimeOffset(2026, 3, 10, 16, 0, 0, TimeSpan.Zero);

        var (start, resetsAt) = SessionBlockBuilder.GetAnchoredWeek(now, (DayOfWeek.Friday, TimeSpan.FromHours(9)), newYork);

        // Friday 6 March 09:00 is still standard time (UTC-5); Friday 13 March 09:00 is daylight time (UTC-4).
        Assert.Equal(new DateTimeOffset(2026, 3, 6, 14, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 3, 13, 13, 0, 0, TimeSpan.Zero), resetsAt);
    }

    /// <summary>
    /// Verifies that the window moves to the previous week while today's anchor time has not been reached yet.
    /// </summary>
    [Fact]
    public void GetAnchoredWeek_AnchorLaterToday_UsesPreviousWeeksOccurrence()
    {
        var now = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

        var (start, resetsAt) = SessionBlockBuilder.GetAnchoredWeek(now, (DayOfWeek.Monday, TimeSpan.FromHours(9)), TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero), resetsAt);
    }

    /// <summary>
    /// Verifies that in a half-hour-offset time zone activity lands in the local hour it happened in, so the hourly trend is not empty.
    /// </summary>
    [Fact]
    public void Build_HalfHourOffsetTimeZone_PlacesActivityInTheLocalHourBucket()
    {
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 1, 10, 10, 0, TimeSpan.Zero), kolkata);
        WriteTranscript(clock, Line(new DateTimeOffset(2026, 8, 1, 9, 20, 0, TimeSpan.Zero), "a"));
        var sut = new RecentActivityBuilder(_cache, new CostCalculator(), clock);

        var recent = sut.Build([_tempFile], TimeSpan.FromHours(2));

        var bucket = recent.HourlyTrend.Single(b => b.HourStart == new DateTimeOffset(2026, 8, 1, 14, 0, 0, TimeSpan.FromMinutes(330)));
        Assert.Equal(1, bucket.Messages);
        Assert.Equal(100, bucket.TotalTokens);
    }

    /// <summary>
    /// A clock fixed at a given moment, with a configurable local time zone.
    /// </summary>
    /// <param name="utcNow">The moment the clock reports.</param>
    /// <param name="zone">The local time zone; UTC when omitted.</param>
    private sealed class FixedClock(DateTimeOffset utcNow, TimeZoneInfo? zone = null) : TimeProvider
    {
        /// <summary>
        /// Gets the local time zone of the clock.
        /// </summary>
        public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;

        /// <summary>
        /// Gets the fixed current time.
        /// </summary>
        /// <returns>The moment the clock was created with.</returns>
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
