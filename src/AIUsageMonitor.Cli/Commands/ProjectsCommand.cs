using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Services;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "projects" command, which shows token usage and estimated cost per project.
/// </summary>
public static class ProjectsCommand
{
    /// <summary>
    /// The length of the range used when no range option is given, ending today.
    /// </summary>
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(30);

    /// <summary>
    /// Creates a new instance of the "projects" command with the specified data service.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve the project usage.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; the system clock when <see langword="null"/>.</param>
    /// <returns>A configured <see cref="Command"/> instance for showing usage per project.</returns>
    public static Command Create(DataService dataService, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var command = new Command("projects", "Show usage per project (default: last 30 days)");
        var rangeOptions = new DateRangeOptions(command, singleDay: false, month: true);
        var listOptions = new ListOptions(command, UsageSorting.ProjectKeys, "tokens");
        var jsonOption = JsonOutput.CreateOption();
        command.Options.Add(jsonOption);
        var setup = new UsageListSetup(clock, DefaultRange, rangeOptions, listOptions, "usage");

        command.SetAction(parseResult => UsageList.Run(
            parseResult,
            setup,
            parseResult.GetValue(jsonOption),
            load: (range, request, progress) => dataService.GetProjectUsage(range.From, range.To, request.Model, request.Project, progress),
            sort: (projects, request) => UsageSorting.SortProjects(projects, request.Sort, request.Top),
            writeJson: (range, total, shown) =>
                JsonOutput.Write(ProjectListReport.Create(range.From, range.To, total, shown), CliJsonContext.Default.ProjectListReport),
            writeTable: (range, total, shown, request) =>
                SpectreRenderer.RenderProjectUsage(shown, total, range.From, range.To, UsageSorting.ShowsMessages(request.Sort))));
        return command;
    }
}
