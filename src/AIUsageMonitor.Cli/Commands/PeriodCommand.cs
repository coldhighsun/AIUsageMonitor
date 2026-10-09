using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Builds the date-range report commands (<c>week</c> and <c>month</c>), which differ only in their default range.
/// </summary>
internal static class PeriodCommand
{
    /// <summary>
    /// Creates a command that shows a usage summary for a date range.
    /// </summary>
    /// <param name="name">The command name.</param>
    /// <param name="description">The command description.</param>
    /// <param name="defaultRange">The length of the range used when no range option is given, ending today.</param>
    /// <param name="allowMonth">Whether the command also accepts <c>--month</c>.</param>
    /// <param name="dataService">The data service used to retrieve the period summary.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance.</returns>
    public static Command Create(
        string name,
        string description,
        TimeSpan defaultRange,
        bool allowMonth,
        DataService dataService,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var command = new Command(name, description);
        var rangeOptions = new DateRangeOptions(command, singleDay: false, month: allowMonth);
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var resolution = DateRangeResolver.Resolve(rangeOptions.Read(parseResult), DateRangeResolver.Today(clock), defaultRange);
            if (resolution.Range is not { } range)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var json = parseResult.GetValue(jsonOption);
            var summary = JsonOutput.Load(json, "Loading usage data...", p => dataService.GetPeriodSummary(range.From, range.To, p));
            JsonOutput.Emit(json, summary, CliJsonContext.Default.PeriodSummary, SpectreRenderer.RenderPeriodSummary);
            return 0;
        });
        return command;
    }
}
