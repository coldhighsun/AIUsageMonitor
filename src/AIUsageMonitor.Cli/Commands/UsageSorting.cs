using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Orders and truncates the project and session lists. Every order is descending by its key (newest first for
/// <c>start</c>), with the identifier as a tiebreak so equal rows keep a stable position.
/// </summary>
internal static class UsageSorting
{
    /// <summary>
    /// The sort key that orders by the number of messages, the one key whose value the tables only show when asked.
    /// </summary>
    public const string MessagesKey = "messages";

    /// <summary>
    /// The sort keys <c>aimon projects</c> accepts.
    /// </summary>
    public static readonly string[] ProjectKeys = ["tokens", "cost", "sessions", MessagesKey];

    /// <summary>
    /// The sort keys <c>aimon sessions --list</c> accepts.
    /// </summary>
    public static readonly string[] SessionKeys = ["tokens", "cost", MessagesKey, "duration", "start"];

    /// <summary>
    /// Determines whether the tables need a Messages column to show the value the rows are ordered by.
    /// </summary>
    /// <param name="sort">The sort key.</param>
    /// <returns><see langword="true"/> when the rows are ordered by the number of messages.</returns>
    public static bool ShowsMessages(string sort)
    {
        return sort == MessagesKey;
    }

    /// <summary>
    /// Orders projects and keeps the first few.
    /// </summary>
    /// <param name="projects">The projects to order.</param>
    /// <param name="sort">One of <see cref="ProjectKeys"/>.</param>
    /// <param name="top">The most projects to keep, or <see langword="null"/> to keep all.</param>
    /// <returns>The ordered, truncated projects.</returns>
    public static List<ProjectUsage> SortProjects(IEnumerable<ProjectUsage> projects, string sort, int? top)
    {
        var ordered = sort switch
        {
            "cost" => projects.OrderByDescending(p => p.EstimatedCost),
            "sessions" => projects.OrderByDescending(p => p.Sessions),
            MessagesKey => projects.OrderByDescending(p => p.Messages),
            _ => projects.OrderByDescending(p => p.TotalTokens),
        };

        return Take(ordered.ThenBy(p => p.ProjectKey, StringComparer.OrdinalIgnoreCase), top);
    }

    /// <summary>
    /// Orders sessions and keeps the first few.
    /// </summary>
    /// <param name="sessions">The sessions to order.</param>
    /// <param name="sort">One of <see cref="SessionKeys"/>.</param>
    /// <param name="top">The most sessions to keep, or <see langword="null"/> to keep all.</param>
    /// <returns>The ordered, truncated sessions.</returns>
    public static List<SessionUsage> SortSessions(IEnumerable<SessionUsage> sessions, string sort, int? top)
    {
        var ordered = sort switch
        {
            "cost" => sessions.OrderByDescending(s => s.EstimatedCost),
            MessagesKey => sessions.OrderByDescending(s => s.Messages),
            "duration" => sessions.OrderByDescending(s => s.Duration),
            "start" => sessions.OrderByDescending(s => s.Start),
            _ => sessions.OrderByDescending(s => s.TotalTokens),
        };

        return Take(ordered.ThenBy(s => s.SessionId, StringComparer.Ordinal), top);
    }

    /// <summary>
    /// Materializes an ordered sequence, keeping only the first few items.
    /// </summary>
    /// <typeparam name="T">The type of the items.</typeparam>
    /// <param name="ordered">The ordered items.</param>
    /// <param name="top">The most items to keep, or <see langword="null"/> to keep all.</param>
    /// <returns>The kept items.</returns>
    private static List<T> Take<T>(IEnumerable<T> ordered, int? top)
    {
        return (top is { } count ? ordered.Take(count) : ordered).ToList();
    }
}
