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
        => new(null, resetsAt, 0, 0, [], confidence, default);

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
    /// A breakdown of what was counted when 40% was read off Claude: 8 dollars of cache reads and 8 of everything else.
    /// </summary>
    private static readonly CostBreakdown AtReading = new(Input: 1m, Output: 2m, CacheRead: 8m, CacheWrite: 5m);

    /// <summary>
    /// Verifies that an explicit percentage becomes a calibration with the cost of the window, whatever was saved.
    /// </summary>
    [Fact]
    public void ResolveCalibration_ExplicitPercent_PairsItWithTheCostSoFar()
    {
        var saved = new UsageCalibration(default, 10);

        var ok = LimitsAnchors.ResolveCalibration(50, saved, AtReading, 0.1m, "Session", new List<string>(), true, out var calibration, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(new UsageCalibration(AtReading, 50), calibration);
    }

    /// <summary>
    /// Verifies that a non-positive percentage is rejected with an error.
    /// </summary>
    [Fact]
    public void ResolveCalibration_NonPositivePercent_ReturnsError()
    {
        var ok = LimitsAnchors.ResolveCalibration(0, null, AtReading, 0.1m, "Session", new List<string>(), true, out var calibration, out var error);

        Assert.False(ok);
        Assert.Null(calibration);
        Assert.NotNull(error);
    }

    /// <summary>
    /// Verifies that a saved calibration is used as it is when no percentage is given, without asking.
    /// </summary>
    [Fact]
    public void ResolveCalibration_NoArgumentAndSavedCalibration_KeepsTheSavedOne()
    {
        var saved = new UsageCalibration(AtReading, 40);

        var ok = LimitsAnchors.ResolveCalibration(null, saved, default, 0.1m, "Session", new List<string>(), true, out var calibration, out _);

        Assert.True(ok);
        Assert.Same(saved, calibration);
    }

    /// <summary>
    /// Verifies that a percentage that cannot be divided by is rejected as an argument instead of crashing later.
    /// </summary>
    /// <param name="percent">The percentage given on the command line.</param>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(1e9)]
    public void ResolveCalibration_UnusablePercent_ReturnsError(double percent)
    {
        var ok = LimitsAnchors.ResolveCalibration(percent, null, AtReading, 0.1m, "Session", new List<string>(), true, out var calibration, out var error);

        Assert.False(ok);
        Assert.Null(calibration);
        Assert.NotNull(error);
    }

    /// <summary>
    /// Verifies that a percentage given while nothing is counted for the window is ignored and the saved calibration is kept.
    /// </summary>
    [Fact]
    public void ResolveCalibration_NothingCountedYet_KeepsTheSavedCalibration()
    {
        var saved = new UsageCalibration(AtReading, 40);
        var notices = new List<string>();

        var ok = LimitsAnchors.ResolveCalibration(30, saved, default, 0.1m, "Session", notices, true, out var calibration, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Same(saved, calibration);
        Assert.Single(notices);
        Assert.Contains("nothing has been counted", notices[0]);
    }

    /// <summary>
    /// Verifies that with nothing counted yet and nothing saved there is no calibration, rather than a useless one.
    /// </summary>
    [Fact]
    public void ResolveCalibration_NothingCountedYetAndNothingSaved_ReturnsNoCalibration()
    {
        var onlyCacheReads = new CostBreakdown(0m, 0m, 5m, 0m);

        var notices = new List<string>();

        var ok = LimitsAnchors.ResolveCalibration(30, null, onlyCacheReads, 0m, "Session", notices, true, out var calibration, out _);

        Assert.True(ok);
        Assert.Null(calibration);
        Assert.Single(notices);
    }

    /// <summary>
    /// Verifies that the notice names the real cause: no usage yet, or a cache read weight that zeroes the usage there is.
    /// </summary>
    [Fact]
    public void DescribeNothingCounted_NoUsageOrZeroWeight_NamesTheCause()
    {
        var empty = LimitsAnchors.DescribeNothingCounted("Session", default, 0.1m);
        var zeroed = LimitsAnchors.DescribeNothingCounted("Weekly", new CostBreakdown(0m, 0m, 5m, 0m), 0m);

        Assert.StartsWith("Session:", empty);
        Assert.Contains("nothing has been counted", empty);
        Assert.StartsWith("Weekly:", zeroed);
        Assert.Contains("counts as zero under a cache read weight of 0", zeroed);
        Assert.Contains("--cache-read-weight", zeroed);
    }

    /// <summary>
    /// Verifies that without an interactive terminal a missing percentage is left unset instead of prompted for.
    /// </summary>
    [Fact]
    public void ResolveCalibration_NoArgumentNothingSavedAndInputRedirected_LeavesItUnsetWithoutPrompting()
    {
        var ok = LimitsAnchors.ResolveCalibration(null, null, AtReading, 0.1m, "Session", new List<string>(), true, out var calibration, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(calibration);
    }

    /// <summary>
    /// Verifies that only a given, unusable percentage argument is reported.
    /// </summary>
    /// <param name="percent">The percentage given on the command line, if any.</param>
    /// <param name="valid">Whether it is acceptable.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData(32.0, true)]
    [InlineData(0.01, true)]
    [InlineData(100000.0, true)]
    [InlineData(0.0, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(100001.0, false)]
    public void ValidatePercentArgument_AnyArgument_ReportsOnlyUnusableOnes(double? percent, bool valid)
    {
        var error = LimitsAnchors.ValidatePercentArgument(percent);

        Assert.Equal(valid, error is null);
    }
}
