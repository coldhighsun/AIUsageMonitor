using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the command for displaying session statistics, or a list of individual sessions, in the AI Usage Monitor CLI application.
/// </summary>
public static class SessionsCommand
{
    /// <summary>
    /// The length of the range used by <c>--list</c> when no range option is given, ending today.
    /// </summary>
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(30);

    /// <summary>
    /// Creates a new instance of the "sessions" command, which displays session statistics or, with <c>--list</c>, a list of sessions.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve session statistics and usage.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing session statistics.</returns>
    public static Command Create(DataService dataService, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var command = new Command("sessions", "Show session statistics, or list individual sessions with --list");
        var listOption = new Option<bool>("--list")
        {
            Description = "List individual sessions (default: last 30 days) instead of showing overall statistics"
        };
        var rangeOptions = new DateRangeOptions(command, singleDay: false, month: true);
        var listOptions = new ListOptions(command, UsageSorting.SessionKeys, "tokens");
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(listOption);
        command.Options.Add(jsonOption);
        var setup = new UsageListSetup(clock, DefaultRange, rangeOptions, listOptions, "sessions");

        command.SetAction(parseResult =>
        {
            var json = parseResult.GetValue(jsonOption);
            if (parseResult.GetValue(listOption))
            {
                return UsageList.Run(
                    parseResult,
                    setup,
                    json,
                    load: (range, request, progress) => dataService.GetSessionUsage(range.From, range.To, request.Model, request.Project, progress),
                    sort: (sessions, request) => UsageSorting.SortSessions(sessions, request.Sort, request.Top),
                    writeJson: (range, total, shown) =>
                        JsonOutput.Write(SessionListReport.Create(range.From, range.To, total, shown), CliJsonContext.Default.SessionListReport),
                    writeTable: (range, total, shown, request) =>
                        SpectreRenderer.RenderSessionList(shown, total, range.From, range.To, UsageSorting.ShowsMessages(request.Sort)));
            }

            if (rangeOptions.Read(parseResult).HasAny || listOptions.AnySupplied(parseResult))
            {
                Console.Error.WriteLine("The date range options, --top, --sort, --project and --model require --list.");
                return 1;
            }

            var stats = JsonOutput.Load(json, "Loading usage data...", dataService.GetSessionStats);
            if (json)
            {
                JsonOutput.Write(SessionStatsReport.Create(stats), CliJsonContext.Default.SessionStatsReport);
            }
            else
            {
                SpectreRenderer.RenderSessionStats(stats);
            }

            return 0;
        });
        return command;
    }
}
