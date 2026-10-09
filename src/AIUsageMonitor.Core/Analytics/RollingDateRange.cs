namespace AIUsageMonitor.Core.Analytics;

/// <summary>
/// Keeps a date range that ends "today" in step with the calendar, so a long-running window does not stay
/// pinned to the day it was opened on.
/// </summary>
public static class RollingDateRange
{
    /// <summary>
    /// Moves a range forward when the day has changed since it was last aligned, but only if it was ending on that
    /// previous day. A range whose end the user picked explicitly (anything other than the previous day) is left alone.
    /// </summary>
    /// <param name="from">The current start of the range.</param>
    /// <param name="to">The current end of the range.</param>
    /// <param name="lastToday">The day the range was last aligned to.</param>
    /// <param name="today">The current day.</param>
    /// <returns>The range to use now; the span between start and end is preserved.</returns>
    public static (DateOnly From, DateOnly To) Advance(DateOnly from, DateOnly to, DateOnly lastToday, DateOnly today)
    {
        if (today <= lastToday || to != lastToday)
        {
            return (from, to);
        }

        var days = today.DayNumber - lastToday.DayNumber;

        return (from.AddDays(days), to.AddDays(days));
    }
}
