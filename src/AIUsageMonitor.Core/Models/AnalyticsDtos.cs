using AIUsageMonitor.Core.Providers.Claude.Models;

namespace AIUsageMonitor.Core.Models;

public sealed record DailySummary(
    DateOnly Date,
    int Messages,
    int Sessions,
    int ToolCalls,
    long TotalTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost);

public sealed record PeriodSummary(
    DateOnly From,
    DateOnly To,
    int TotalMessages,
    int TotalSessions,
    int TotalToolCalls,
    long TotalTokens,
    decimal EstimatedCost,
    List<DailySummary> DailyBreakdown);

public sealed record ModelDistribution(
    string ModelName,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    long TotalTokens,
    double Percentage,
    decimal EstimatedCost);

public sealed record HourlyActivity(int Hour, long TotalTokens);

public sealed record HourBucket(DateTimeOffset HourStart, int Messages, long TotalTokens);

public sealed record RecentActivitySummary(
    TimeSpan Window,
    int Messages,
    int Sessions,
    int ToolCalls,
    long TotalTokens,
    Dictionary<string, long> TokensByModel,
    decimal EstimatedCost,
    List<HourBucket> HourlyTrend);

public sealed record SessionStats(
    int Total,
    TimeSpan AvgDuration,
    double AvgMessages,
    TimeSpan LongestDuration,
    string? LongestSessionId);

public sealed record SessionSummary(
    string SessionId,
    string? Project,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    TimeSpan Duration,
    int MessageCount,
    long TotalTokens,
    Dictionary<string, long> TokensByModel);

public sealed record ExportPayload(
    int TotalSessions,
    int TotalMessages,
    List<DailyActivity> DailyActivity,
    List<DailyModelTokens> DailyModelTokens,
    List<ExportModelDistribution> ModelDistribution);

public sealed record ExportModelDistribution(
    string ModelName,
    long TotalTokens,
    double Percentage,
    decimal EstimatedCost);