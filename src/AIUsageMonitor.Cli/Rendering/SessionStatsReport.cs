using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// The JSON shape of <see cref="SessionStats"/>, with durations as plain seconds so scripts need no duration parsing.
/// </summary>
/// <param name="Total">The number of sessions.</param>
/// <param name="AvgDurationSeconds">The average session length, in seconds.</param>
/// <param name="AvgMessages">The average number of messages per session.</param>
/// <param name="LongestDurationSeconds">The longest session length, in seconds.</param>
/// <param name="LongestSessionId">The id of the longest session, if known.</param>
internal sealed record SessionStatsReport(
    int Total,
    double AvgDurationSeconds,
    double AvgMessages,
    double LongestDurationSeconds,
    string? LongestSessionId)
{
    /// <summary>
    /// Converts session statistics to their JSON shape.
    /// </summary>
    /// <param name="stats">The statistics to convert.</param>
    /// <returns>The JSON-shaped report.</returns>
    public static SessionStatsReport From(SessionStats stats)
    {
        return new SessionStatsReport(
            stats.Total,
            stats.AvgDuration.TotalSeconds,
            stats.AvgMessages,
            stats.LongestDuration.TotalSeconds,
            stats.LongestSessionId);
    }
}
