using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "week" command, which shows a usage summary for the last 7 days or a chosen date range.
/// </summary>
public static class WeekCommand
{
    /// <summary>
    /// Creates a new instance of the "week" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the period summary.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing a weekly summary.</returns>
    public static Command Create(DataService dataService, TimeProvider? timeProvider = null)
    {
        return PeriodCommand.Create(
            "week", "Show usage for a date range (default: last 7 days)", TimeSpan.FromDays(7), allowMonth: false, dataService, timeProvider);
    }
}
