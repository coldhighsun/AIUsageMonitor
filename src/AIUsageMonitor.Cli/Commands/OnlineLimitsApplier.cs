using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Turns what Anthropic's account API said (<see cref="OnlineLimitsResult"/>) into what the limits view needs: the real
/// reset times that pin the two windows, and the calibrations that keep the local estimate close to the exact percentage
/// for the moments when no live reading can be had. Pure, so it can be tested without a terminal or a network.
/// </summary>
internal static class OnlineLimitsApplier
{
    /// <summary>
    /// Gets the real reset time of the current session window, when the reading names one that has not passed.
    /// A reading kept on disk counts too: a reset time stays true until it passes.
    /// </summary>
    /// <param name="online">What the account API said, if online reading is on.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The reset time, or <see langword="null"/> when there is none to use.</returns>
    internal static DateTimeOffset? SessionAnchor(OnlineLimitsResult? online, DateTimeOffset now)
        => online?.Limits?.Session?.ResetsAt is { } reset && reset > now ? reset : null;

    /// <summary>
    /// Gets the weekly reset as the day of the week and local time of day the weekly window is pinned to, rounded to the
    /// minute (the API names the instant to the second or beyond).
    /// </summary>
    /// <param name="online">What the account API said, if online reading is on.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The anchor, or <see langword="null"/> when the reading names no weekly reset that has not passed.</returns>
    internal static (DayOfWeek Day, TimeSpan TimeOfDay)? WeekAnchor(OnlineLimitsResult? online, DateTimeOffset now)
    {
        if (online?.Limits?.Week?.ResetsAt is not { } reset || reset <= now)
        {
            return null;
        }

        var rounded = reset.ToLocalTime().AddSeconds(30);
        return (rounded.DayOfWeek, new TimeSpan(rounded.Hour, rounded.Minute, 0));
    }

    /// <summary>
    /// Gets the exact usage percentage of a quota when the reading is live and the percentage can be calibrated against.
    /// </summary>
    /// <param name="online">What the account API said, if online reading is on.</param>
    /// <param name="quota">Which quota to take.</param>
    /// <returns>The percentage, for example 32 for '32%', or <see langword="null"/>.</returns>
    internal static double? LivePercent(OnlineLimitsResult? online, Func<OnlineLimits, OnlineQuota?> quota)
        => online is { IsLive: true, Limits: { } limits } && quota(limits) is { } q && UsageCalibration.IsValidPercent(q.Percent) ? q.Percent : null;

    /// <summary>
    /// Re-bases the calibration on the exact percentage of a live reading, so the estimate that takes over when no live
    /// reading can be had starts from the latest exact one.
    /// </summary>
    /// <param name="current">The calibration in effect, if any.</param>
    /// <param name="exactPercent">The live exact percentage, if there is one (see <see cref="LivePercent"/>).</param>
    /// <param name="costSoFar">The estimated cost of the window by kind of token right now.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect.</param>
    /// <returns>The new calibration, or <paramref name="current"/> when nothing counted yet or there is no exact percentage.</returns>
    internal static UsageCalibration? Recalibrate(
        UsageCalibration? current, double? exactPercent, CostBreakdown costSoFar, decimal cacheReadWeight)
        => exactPercent is { } percent && QuotaCost.Compute(costSoFar, cacheReadWeight) > 0m
            ? new UsageCalibration(costSoFar, percent)
            : current;
}
