using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Models;

/// <summary>
/// Represents a summary of usage activity for a single calendar day.
/// </summary>
/// <param name="Date">The calendar date the summary covers.</param>
/// <param name="Messages">The total number of messages sent on this date.</param>
/// <param name="Sessions">The total number of sessions active on this date.</param>
/// <param name="ToolCalls">The total number of tool calls made on this date.</param>
/// <param name="TotalTokens">The total number of tokens used on this date.</param>
/// <param name="TokensByModel">A mapping of model name to the number of tokens consumed by that model.</param>
/// <param name="EstimatedCost">The estimated monetary cost of usage for this date.</param>
public sealed record DailySummary(
    DateOnly Date,
    int Messages,
    int Sessions,
    int ToolCalls,
    long TotalTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost);

/// <summary>
/// Represents an aggregated summary of usage activity over a date range.
/// </summary>
/// <param name="From">The start date of the period (inclusive).</param>
/// <param name="To">The end date of the period (inclusive).</param>
/// <param name="TotalMessages">The total number of messages sent during the period.</param>
/// <param name="TotalSessions">The total number of sessions active during the period.</param>
/// <param name="TotalToolCalls">The total number of tool calls made during the period.</param>
/// <param name="TotalTokens">The total number of tokens used during the period.</param>
/// <param name="EstimatedCost">The estimated monetary cost of usage for the period.</param>
/// <param name="DailyBreakdown">A per-day breakdown of usage within the period.</param>
public sealed record PeriodSummary(
    DateOnly From,
    DateOnly To,
    int TotalMessages,
    int TotalSessions,
    int TotalToolCalls,
    long TotalTokens,
    decimal EstimatedCost,
    List<DailySummary> DailyBreakdown);

/// <summary>
/// Represents the token usage distribution for a specific model.
/// </summary>
/// <param name="ModelName">The name of the model.</param>
/// <param name="InputTokens">The number of input tokens consumed by the model.</param>
/// <param name="OutputTokens">The number of output tokens produced by the model.</param>
/// <param name="CacheReadTokens">The number of tokens read from cache.</param>
/// <param name="CacheCreationTokens">The number of tokens used to create cache entries.</param>
/// <param name="TotalTokens">The total number of tokens consumed by the model.</param>
/// <param name="Percentage">The percentage of overall token usage attributed to this model.</param>
/// <param name="EstimatedCost">The estimated monetary cost of usage for this model.</param>
public sealed record ModelDistribution(
    string ModelName,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    double Percentage,
    decimal EstimatedCost);

/// <summary>
/// Represents the total token usage for a specific hour of the day.
/// </summary>
/// <param name="Hour">The hour of the day, in the range 0-23.</param>
/// <param name="TotalTokens">The total number of tokens used during that hour.</param>
public sealed record HourlyActivity(int Hour, long TotalTokens);

/// <summary>
/// Represents a bucket of usage activity aggregated over a single hour.
/// </summary>
/// <param name="HourStart">The start timestamp of the hour bucket.</param>
/// <param name="Messages">The number of messages sent during the hour.</param>
/// <param name="TotalTokens">The total number of tokens used during the hour.</param>
public sealed record HourBucket(DateTimeOffset HourStart, int Messages, long TotalTokens);

/// <summary>
/// Represents a summary of recent usage activity within a sliding time window.
/// </summary>
/// <param name="Window">The duration of the time window covered by the summary.</param>
/// <param name="Messages">The total number of messages sent within the window.</param>
/// <param name="Sessions">The total number of sessions active within the window.</param>
/// <param name="ToolCalls">The total number of tool calls made within the window.</param>
/// <param name="TotalTokens">The total number of tokens used within the window.</param>
/// <param name="TokensByModel">A mapping of model name to the number of tokens consumed by that model.</param>
/// <param name="EstimatedCost">The estimated monetary cost of usage within the window.</param>
/// <param name="HourlyTrend">A per-hour breakdown of usage within the window.</param>
public sealed record RecentActivitySummary(
    TimeSpan Window,
    int Messages,
    int Sessions,
    int ToolCalls,
    long TotalTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost,
    List<HourBucket> HourlyTrend);

/// <summary>
/// Represents aggregated statistics across all sessions.
/// </summary>
/// <param name="Total">The total number of sessions.</param>
/// <param name="AvgDuration">The average session duration.</param>
/// <param name="AvgMessages">The average number of messages per session.</param>
/// <param name="LongestDuration">The duration of the longest session.</param>
/// <param name="LongestSessionId">The identifier of the longest session, or <see langword="null"/> if unavailable.</param>
public sealed record SessionStats(
    int Total,
    TimeSpan AvgDuration,
    double AvgMessages,
    TimeSpan LongestDuration,
    string? LongestSessionId);

/// <summary>
/// Represents a summary of usage activity for a single session.
/// </summary>
/// <param name="SessionId">The unique identifier of the session.</param>
/// <param name="Project">The name of the project associated with the session, or <see langword="null"/> if unavailable.</param>
/// <param name="StartTime">The timestamp when the session started.</param>
/// <param name="EndTime">The timestamp when the session ended.</param>
/// <param name="Duration">The total duration of the session.</param>
/// <param name="MessageCount">The total number of messages sent during the session.</param>
/// <param name="TotalTokens">The total number of tokens used during the session.</param>
/// <param name="TokensByModel">A mapping of model name to the number of tokens consumed by that model.</param>
public sealed record SessionSummary(
    string SessionId,
    string? Project,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    TimeSpan Duration,
    int MessageCount,
    long TotalTokens,
    Dictionary<string, long> TokensByModel);

/// <summary>
/// Represents the full data payload produced when exporting usage data.
/// </summary>
/// <param name="TotalSessions">The total number of sessions included in the export.</param>
/// <param name="TotalMessages">The total number of messages included in the export.</param>
/// <param name="DailyActivity">The per-day activity records included in the export.</param>
/// <param name="DailyModelTokens">The per-day, per-model token usage records included in the export.</param>
/// <param name="ModelDistribution">The overall token usage distribution across models included in the export.</param>
public sealed record ExportPayload(
    int TotalSessions,
    int TotalMessages,
    List<DailyActivity> DailyActivity,
    List<DailyModelTokens> DailyModelTokens,
    List<ExportModelDistribution> ModelDistribution);

/// <summary>
/// Represents the token usage distribution for a specific model within an export payload.
/// </summary>
/// <param name="ModelName">The name of the model.</param>
/// <param name="TotalTokens">The total number of tokens consumed by the model.</param>
/// <param name="Percentage">The percentage of overall token usage attributed to this model.</param>
/// <param name="EstimatedCost">The estimated monetary cost of usage for this model.</param>
public sealed record ExportModelDistribution(
    string ModelName,
    long TotalTokens,
    double Percentage,
    decimal EstimatedCost);