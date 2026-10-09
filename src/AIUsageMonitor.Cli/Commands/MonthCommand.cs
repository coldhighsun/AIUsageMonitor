using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "month" command, which shows a usage summary for the last 30 days, a chosen date range or a calendar month.
/// </summary>
public static class MonthCommand
{
    /// <summary>
    /// Creates a new instance of the "month" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the period summary.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing a monthly summary.</returns>
    public static Command Create(DataService dataService, TimeProvider? timeProvider = null)
    {
        return PeriodCommand.Create(
            "month", "Show usage for a date range or calendar month (default: last 30 days)", TimeSpan.FromDays(30), allowMonth: true, dataService, timeProvider);
    }
}
