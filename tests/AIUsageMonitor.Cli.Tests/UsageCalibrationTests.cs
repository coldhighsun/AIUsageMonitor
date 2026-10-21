using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="UsageCalibration"/>, the reading a usage limit is derived from.
/// </summary>
public class UsageCalibrationTests
{
    /// <summary>
    /// A breakdown of what was counted when 40% was read off Claude: 8 dollars of cache reads and 8 of everything else.
    /// </summary>
    private static readonly CostBreakdown AtReading = new(Input: 1m, Output: 2m, CacheRead: 8m, CacheWrite: 5m);

    /// <summary>
    /// Verifies that the limit is the weighted cost at the reading divided by the percentage read.
    /// </summary>
    [Fact]
    public void DeriveLimit_FullWeight_IsTotalCostOverFraction()
    {
        var calibration = new UsageCalibration(AtReading, 40);

        var limit = calibration.DeriveLimit(1m);

        Assert.Equal(40m, limit);
    }

    /// <summary>
    /// Verifies that changing the weight re-derives the limit from the same reading, with no new percentage.
    /// </summary>
    [Fact]
    public void DeriveLimit_LowerWeight_ShrinksTheLimitFromTheSameReading()
    {
        var calibration = new UsageCalibration(AtReading, 40);

        var limit = calibration.DeriveLimit(0.5m);

        Assert.Equal(30m, limit);
    }

    /// <summary>
    /// Verifies that the reading itself is the percentage under any weight, so the bar agrees with Claude at the moment it was read.
    /// </summary>
    /// <param name="weight">The cache read weight.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(0.1)]
    [InlineData(1)]
    public void PercentOf_CostAtTheReading_IsThePercentageRead(double weight)
    {
        var calibration = new UsageCalibration(AtReading, 40);

        var percent = calibration.PercentOf(AtReading, (decimal)weight);

        Assert.NotNull(percent);
        Assert.Equal(40m, percent.Value, precision: 6);
    }

    /// <summary>
    /// Verifies that calibrations whose percentage cannot be divided by are dropped.
    /// </summary>
    /// <param name="percent">The percentage held by the calibration.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(0.001)]
    [InlineData(1e9)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void OrNullIfInvalid_UnusablePercent_ReturnsNull(double percent)
    {
        var result = UsageCalibration.OrNullIfInvalid(new UsageCalibration(AtReading, percent));

        Assert.Null(result);
    }

    /// <summary>
    /// Verifies that a usable calibration, and no calibration at all, pass through unchanged.
    /// </summary>
    [Fact]
    public void OrNullIfInvalid_UsableOrMissing_ReturnsItUnchanged()
    {
        var calibration = new UsageCalibration(AtReading, 40);

        Assert.Same(calibration, UsageCalibration.OrNullIfInvalid(calibration));
        Assert.Null(UsageCalibration.OrNullIfInvalid(null));
    }

    /// <summary>
    /// Verifies that no limit is derived when nothing counted at the reading under the weight, instead of a one-cent limit.
    /// </summary>
    [Fact]
    public void DeriveLimit_NothingCountedAtTheReading_ReturnsNull()
    {
        var onlyCacheReads = new UsageCalibration(new CostBreakdown(0m, 0m, 5m, 0m), 40);

        Assert.Null(onlyCacheReads.DeriveLimit(0m));
        Assert.Null(new UsageCalibration(default, 40).DeriveLimit(0.1m));
        Assert.Equal(12.5m, onlyCacheReads.DeriveLimit(1m));
    }

    /// <summary>
    /// Verifies that there is no percentage to show for a cost while there is no limit to measure it against.
    /// </summary>
    [Fact]
    public void PercentOf_NoLimit_ReturnsNull()
    {
        var onlyCacheReads = new UsageCalibration(new CostBreakdown(0m, 0m, 5m, 0m), 40);

        Assert.Null(onlyCacheReads.PercentOf(AtReading, 0m));
    }

    /// <summary>
    /// Verifies that a small cost is not raised to a floor, so the percentage at the reading stays the one that was read.
    /// </summary>
    [Fact]
    public void DeriveLimit_SmallCostAtTheReading_IsNotRaisedToAFloor()
    {
        var calibration = new UsageCalibration(new CostBreakdown(Input: 0.002m, Output: 0m, CacheRead: 0m, CacheWrite: 0m), 50);

        Assert.Equal(0.004m, calibration.DeriveLimit(0.1m));
        Assert.Equal(50m, calibration.PercentOf(calibration.Breakdown, 0.1m)!.Value, precision: 6);
    }

    /// <summary>
    /// Verifies that a calibration whose percentage cannot be divided by gives no limit instead of throwing.
    /// </summary>
    [Fact]
    public void DeriveLimit_UnusablePercent_ReturnsNull()
    {
        Assert.Null(new UsageCalibration(AtReading, 0).DeriveLimit(1m));
        Assert.Null(new UsageCalibration(AtReading, double.NaN).DeriveLimit(1m));
    }
}
