using AIUsageMonitor.Core.Analytics;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Analytics;

/// <summary>
/// Tests for <see cref="RollingDateRange"/>.
/// </summary>
public class RollingDateRangeTests
{
    /// <summary>
    /// Verifies that a range ending on the previous day moves forward with the calendar, keeping its span.
    /// </summary>
    [Fact]
    public void Advance_RangeEndsOnLastToday_ShiftsBothEnds()
    {
        var (from, to) = RollingDateRange.Advance(new(2026, 9, 10), new(2026, 10, 9), new(2026, 10, 9), new(2026, 10, 10));

        Assert.Equal(new DateOnly(2026, 9, 11), from);
        Assert.Equal(new DateOnly(2026, 10, 10), to);
    }

    /// <summary>
    /// Verifies that several skipped days (e.g. a machine that slept) are all caught up at once.
    /// </summary>
    [Fact]
    public void Advance_SeveralDaysPassed_ShiftsByTheFullDifference()
    {
        var (from, to) = RollingDateRange.Advance(new(2026, 10, 3), new(2026, 10, 9), new(2026, 10, 9), new(2026, 10, 12));

        Assert.Equal(new DateOnly(2026, 10, 6), from);
        Assert.Equal(new DateOnly(2026, 10, 12), to);
    }

    /// <summary>
    /// Verifies that a range whose end was picked explicitly by the user is not moved.
    /// </summary>
    [Fact]
    public void Advance_RangeEndsOnAnotherDay_LeavesRangeUnchanged()
    {
        var (from, to) = RollingDateRange.Advance(new(2026, 8, 1), new(2026, 8, 31), new(2026, 10, 9), new(2026, 10, 10));

        Assert.Equal(new DateOnly(2026, 8, 1), from);
        Assert.Equal(new DateOnly(2026, 8, 31), to);
    }

    /// <summary>
    /// Verifies that nothing changes while it is still the same day.
    /// </summary>
    [Fact]
    public void Advance_SameDay_LeavesRangeUnchanged()
    {
        var (from, to) = RollingDateRange.Advance(new(2026, 9, 10), new(2026, 10, 9), new(2026, 10, 9), new(2026, 10, 9));

        Assert.Equal(new DateOnly(2026, 9, 10), from);
        Assert.Equal(new DateOnly(2026, 10, 9), to);
    }

    /// <summary>
    /// Verifies that a clock that moved backwards does not drag the range back with it.
    /// </summary>
    [Fact]
    public void Advance_ClockWentBackwards_LeavesRangeUnchanged()
    {
        var (from, to) = RollingDateRange.Advance(new(2026, 9, 10), new(2026, 10, 9), new(2026, 10, 9), new(2026, 10, 8));

        Assert.Equal(new DateOnly(2026, 9, 10), from);
        Assert.Equal(new DateOnly(2026, 10, 9), to);
    }
}
