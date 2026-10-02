using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for the pure logic in <see cref="LimitsAnchors"/> used by the watch recalibration hotkey.
/// </summary>
public class LimitsAnchorsTests
{
    /// <summary>
    /// A fixed current time so the tests do not depend on the wall clock.
    /// </summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies that the reset currently in effect wins over the persisted one.
    /// </summary>
    [Fact]
    public void SelectSessionResetDefault_CurrentResetPresent_ReturnsCurrent()
    {
        var current = Now.AddHours(2);
        var saved = Now.AddHours(1);

        var result = LimitsAnchors.SelectSessionResetDefault(current, saved, Now);

        Assert.Equal(current, result);
    }

    /// <summary>
    /// Verifies that a still-upcoming persisted reset is offered when nothing is in effect.
    /// </summary>
    [Fact]
    public void SelectSessionResetDefault_NoCurrentAndSavedUpcoming_ReturnsSaved()
    {
        var saved = Now.AddHours(1);

        var result = LimitsAnchors.SelectSessionResetDefault(null, saved, Now);

        Assert.Equal(saved, result);
    }

    /// <summary>
    /// Verifies that an elapsed persisted reset is never offered as the default.
    /// </summary>
    [Fact]
    public void SelectSessionResetDefault_NoCurrentAndSavedElapsed_ReturnsNull()
    {
        var saved = Now.AddMinutes(-1);

        var result = LimitsAnchors.SelectSessionResetDefault(null, saved, Now);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a persisted reset exactly at the current time counts as elapsed.
    /// </summary>
    [Fact]
    public void SelectSessionResetDefault_SavedEqualsNow_ReturnsNull()
    {
        var result = LimitsAnchors.SelectSessionResetDefault(null, Now, Now);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that nothing is offered when neither value exists.
    /// </summary>
    [Fact]
    public void SelectSessionResetDefault_NothingAvailable_ReturnsNull()
    {
        var result = LimitsAnchors.SelectSessionResetDefault(null, null, Now);

        Assert.Null(result);
    }

    /// <summary>
    /// Builds a session window with the given reset time and confidence.
    /// </summary>
    private static UsageWindowSummary Window(DateTimeOffset? resetsAt, WindowConfidence confidence)
        => new(null, resetsAt, 0, 0, [], 0m, confidence);

    /// <summary>
    /// Verifies that an upcoming estimated reset is offered as the default.
    /// </summary>
    [Fact]
    public void SelectShownSessionReset_EstimatedUpcoming_ReturnsReset()
    {
        var reset = Now.AddHours(2);

        var result = LimitsAnchors.SelectShownSessionReset(Window(reset, WindowConfidence.Estimated), Now);

        Assert.Equal(reset, result);
    }

    /// <summary>
    /// Verifies that an upcoming confirmed reset is offered as the default.
    /// </summary>
    [Fact]
    public void SelectShownSessionReset_ConfirmedUpcoming_ReturnsReset()
    {
        var reset = Now.AddHours(2);

        var result = LimitsAnchors.SelectShownSessionReset(Window(reset, WindowConfidence.Confirmed), Now);

        Assert.Equal(reset, result);
    }

    /// <summary>
    /// Verifies that an elapsed reset is not offered.
    /// </summary>
    [Fact]
    public void SelectShownSessionReset_Elapsed_ReturnsNull()
    {
        var result = LimitsAnchors.SelectShownSessionReset(
            Window(Now.AddMinutes(-1), WindowConfidence.Estimated), Now);

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that nothing is offered when the window has no reset time or does not exist.
    /// </summary>
    [Fact]
    public void SelectShownSessionReset_NoResetOrNoWindow_ReturnsNull()
    {
        Assert.Null(LimitsAnchors.SelectShownSessionReset(Window(null, WindowConfidence.Unknown), Now));
        Assert.Null(LimitsAnchors.SelectShownSessionReset(null, Now));
    }

    /// <summary>
    /// Verifies the limit is cost divided by the entered fraction.
    /// </summary>
    [Fact]
    public void DeriveCostLimit_CostAndPercent_ReturnsCostOverFraction()
    {
        var result = LimitsAnchors.DeriveCostLimit(costSoFar: 8m, progressPercent: 32);

        Assert.Equal(25m, result);
    }

    /// <summary>
    /// Verifies the limit never drops below a cent when no cost has accrued yet.
    /// </summary>
    [Fact]
    public void DeriveCostLimit_ZeroCost_ReturnsOneCentFloor()
    {
        var result = LimitsAnchors.DeriveCostLimit(costSoFar: 0m, progressPercent: 5);

        Assert.Equal(0.01m, result);
    }

    /// <summary>
    /// Verifies an explicit percentage argument is converted using the supplied cost.
    /// </summary>
    [Fact]
    public void ResolveTokenLimit_ExplicitPercent_DerivesLimitFromCost()
    {
        var ok = LimitsAnchors.ResolveTokenLimit(50, savedLimit: 99m, costSoFar: 4m, "Session", out var limit, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(8m, limit);
    }

    /// <summary>
    /// Verifies a non-positive percentage is rejected with an error.
    /// </summary>
    [Fact]
    public void ResolveTokenLimit_NonPositivePercent_ReturnsError()
    {
        var ok = LimitsAnchors.ResolveTokenLimit(0, savedLimit: null, costSoFar: 4m, "Session", out var limit, out var error);

        Assert.False(ok);
        Assert.Null(limit);
        Assert.NotNull(error);
    }
}
