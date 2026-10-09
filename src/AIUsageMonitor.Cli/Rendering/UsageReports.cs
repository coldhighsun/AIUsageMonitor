using AIUsageMonitor.Core.Models;
using System.Globalization;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// The JSON shape of one project in <see cref="ProjectListReport"/>.
/// </summary>
/// <param name="ProjectKey">The name of the folder under <c>projects/</c>.</param>
/// <param name="ProjectPath">The working directory of the project, if one was recorded.</param>
/// <param name="Sessions">The number of sessions.</param>
/// <param name="Messages">The number of counted transcript lines.</param>
/// <param name="ToolCalls">The number of tool calls.</param>
/// <param name="TotalTokens">The total tokens used.</param>
/// <param name="TokensByModel">The tokens used per model.</param>
/// <param name="EstimatedCost">The estimated cost, in US dollars.</param>
/// <param name="FirstActivity">The time of the first counted message.</param>
/// <param name="LastActivity">The time of the last counted message.</param>
internal sealed record ProjectUsageItem(
    string ProjectKey,
    string? ProjectPath,
    int Sessions,
    int Messages,
    int ToolCalls,
    long TotalTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost,
    DateTimeOffset FirstActivity,
    DateTimeOffset LastActivity)
{
    /// <summary>
    /// Converts project usage to its JSON shape.
    /// </summary>
    /// <param name="project">The project usage.</param>
    /// <returns>The JSON-shaped item.</returns>
    public static ProjectUsageItem Create(ProjectUsage project)
    {
        return new ProjectUsageItem(
            project.ProjectKey, project.ProjectPath, project.Sessions, project.Messages, project.ToolCalls,
            project.TotalTokens, project.TokensByModel, project.EstimatedCost, project.FirstActivity, project.LastActivity);
    }
}

/// <summary>
/// The JSON shape printed by <c>aimon projects --json</c>.
/// </summary>
/// <param name="From">The first day of the range, as <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last day of the range, as <c>yyyy-MM-dd</c>.</param>
/// <param name="TotalProjects">The number of projects that matched, before <c>--top</c> was applied.</param>
/// <param name="Projects">The projects shown, in the requested order.</param>
internal sealed record ProjectListReport(string From, string To, int TotalProjects, List<ProjectUsageItem> Projects)
{
    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <param name="from">The first day of the range.</param>
    /// <param name="to">The last day of the range.</param>
    /// <param name="totalProjects">The number of projects that matched.</param>
    /// <param name="shown">The projects shown, in order.</param>
    /// <returns>The report.</returns>
    public static ProjectListReport Create(DateOnly from, DateOnly to, int totalProjects, IEnumerable<ProjectUsage> shown)
    {
        return new ProjectListReport(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            totalProjects,
            shown.Select(ProjectUsageItem.Create).ToList());
    }
}

/// <summary>
/// The JSON shape of one session in <see cref="SessionListReport"/>, with the duration as plain seconds.
/// </summary>
/// <param name="SessionId">The session id.</param>
/// <param name="ProjectKey">The name of the folder under <c>projects/</c>.</param>
/// <param name="ProjectPath">The working directory, if one was recorded.</param>
/// <param name="Start">The time of the first counted message.</param>
/// <param name="End">The time of the last counted message.</param>
/// <param name="DurationSeconds">The wall-clock time between the first and last message, idle gaps included, in seconds.</param>
/// <param name="Messages">The number of counted transcript lines.</param>
/// <param name="ToolCalls">The number of tool calls.</param>
/// <param name="TotalTokens">The total tokens used.</param>
/// <param name="InputTokens">The input tokens used.</param>
/// <param name="OutputTokens">The output tokens used.</param>
/// <param name="CacheReadTokens">The tokens read from the prompt cache.</param>
/// <param name="CacheWriteTokens">The tokens written to the prompt cache.</param>
/// <param name="TokensByModel">The tokens used per model.</param>
/// <param name="EstimatedCost">The estimated cost, in US dollars.</param>
internal sealed record SessionUsageItem(
    string SessionId,
    string ProjectKey,
    string? ProjectPath,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    int Messages,
    int ToolCalls,
    long TotalTokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost)
{
    /// <summary>
    /// Converts session usage to its JSON shape.
    /// </summary>
    /// <param name="session">The session usage.</param>
    /// <returns>The JSON-shaped item.</returns>
    public static SessionUsageItem Create(SessionUsage session)
    {
        return new SessionUsageItem(
            session.SessionId, session.ProjectKey, session.ProjectPath, session.Start, session.End,
            session.Duration.TotalSeconds, session.Messages, session.ToolCalls, session.TotalTokens, session.InputTokens,
            session.OutputTokens, session.CacheReadTokens, session.CacheWriteTokens, session.TokensByModel, session.EstimatedCost);
    }
}

/// <summary>
/// The JSON shape printed by <c>aimon sessions --list --json</c>.
/// </summary>
/// <param name="From">The first day of the range, as <c>yyyy-MM-dd</c>.</param>
/// <param name="To">The last day of the range, as <c>yyyy-MM-dd</c>.</param>
/// <param name="TotalSessions">The number of sessions that matched, before <c>--top</c> was applied.</param>
/// <param name="Sessions">The sessions shown, in the requested order.</param>
internal sealed record SessionListReport(string From, string To, int TotalSessions, List<SessionUsageItem> Sessions)
{
    /// <summary>
    /// Builds the report.
    /// </summary>
    /// <param name="from">The first day of the range.</param>
    /// <param name="to">The last day of the range.</param>
    /// <param name="totalSessions">The number of sessions that matched.</param>
    /// <param name="shown">The sessions shown, in order.</param>
    /// <returns>The report.</returns>
    public static SessionListReport Create(DateOnly from, DateOnly to, int totalSessions, IEnumerable<SessionUsage> shown)
    {
        return new SessionListReport(
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            totalSessions,
            shown.Select(SessionUsageItem.Create).ToList());
    }
}
