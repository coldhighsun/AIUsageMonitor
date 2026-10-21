using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="LimitsStartup"/>, which decides the weight, the calibrations and the settings to save when
/// <c>watch --view limits</c> starts. They all run as if the input were redirected, so nothing can prompt.
/// </summary>
public class LimitsStartupTests
{
    /// <summary>
    /// The cost of a window that has usage of every kind.
    /// </summary>
    private static readonly CostBreakdown Used = new(Input: 1m, Output: 2m, CacheRead: 10m, CacheWrite: 3m);

    /// <summary>
    /// Settings that already hold both calibrations.
    /// </summary>
    private static readonly LimitsSettings WithCalibrations = LimitsSettings.Empty with
    {
        SessionCalibration = new UsageCalibration(Used, 40),
        WeekCalibration = new UsageCalibration(Used, 10),
    };

    /// <summary>
    /// Verifies that explicit percentages become calibrations with the cost of their windows, and a given weight is applied and saved.
    /// </summary>
    [Fact]
    public void Resolve_PercentagesAndWeightGiven_CalibratesAndSavesTheWeight()
    {
        var result = LimitsStartup.Resolve(LimitsSettings.Empty, 25, 5, 0.3m, false, Used, Used, true);

        Assert.Null(result.Error);
        Assert.Equal(0.3m, result.CacheReadWeight);
        Assert.Equal(new UsageCalibration(Used, 25), result.SessionCalibration);
        Assert.Equal(new UsageCalibration(Used, 5), result.WeekCalibration);
        Assert.Equal(0.3m, result.ToSave.CacheReadWeight);
        Assert.Equal(result.SessionCalibration, result.ToSave.SessionCalibration);
        Assert.Equal(result.WeekCalibration, result.ToSave.WeekCalibration);
        Assert.Empty(result.Notices);
    }

    /// <summary>
    /// Verifies that saved calibrations and a saved weight are kept when nothing is given, and the default is not saved.
    /// </summary>
    [Fact]
    public void Resolve_NothingGiven_KeepsWhatIsSaved()
    {
        var saved = WithCalibrations with { CacheReadWeight = 0.4m };

        var result = LimitsStartup.Resolve(saved, null, null, null, false, Used, Used, true);

        Assert.Equal(0.4m, result.CacheReadWeight);
        Assert.Equal(saved.SessionCalibration, result.SessionCalibration);
        Assert.Equal(saved.WeekCalibration, result.WeekCalibration);
        Assert.Equal(saved, result.ToSave);
    }

    /// <summary>
    /// Verifies that with no weight given or saved the default applies without being saved.
    /// </summary>
    [Fact]
    public void Resolve_NoWeightGivenOrSaved_UsesTheDefaultAndDoesNotSaveIt()
    {
        var result = LimitsStartup.Resolve(WithCalibrations, null, null, null, false, Used, Used, true);

        Assert.Equal(QuotaCost.DefaultCacheReadWeight, result.CacheReadWeight);
        Assert.Null(result.ToSave.CacheReadWeight);
    }

    /// <summary>
    /// Verifies that resetting forgets the saved weight, uses the default and keeps the calibrations.
    /// </summary>
    [Fact]
    public void Resolve_Reset_ForgetsTheSavedWeightAndKeepsTheCalibrations()
    {
        var saved = WithCalibrations with { CacheReadWeight = 0.4m };

        var result = LimitsStartup.Resolve(saved, null, null, null, true, Used, Used, true);

        Assert.Equal(QuotaCost.DefaultCacheReadWeight, result.CacheReadWeight);
        Assert.Null(result.ToSave.CacheReadWeight);
        Assert.Equal(saved.SessionCalibration, result.ToSave.SessionCalibration);
        Assert.Equal(saved.WeekCalibration, result.ToSave.WeekCalibration);
    }

    /// <summary>
    /// Verifies that limits saved by an older version are reported once, naming what has to be passed again, and are not saved again.
    /// </summary>
    [Fact]
    public void Resolve_LegacyLimitsAndNoCalibration_ReportsThemAndClearsThemInTheSettingsToSave()
    {
        var saved = LimitsSettings.Empty with { SessionCostLimit = 12.5m, WeekCostLimit = 80m };

        var result = LimitsStartup.Resolve(saved, null, null, null, false, Used, Used, true);

        Assert.Single(result.Notices);
        Assert.Contains("older version", result.Notices[0]);
        Assert.Contains("--session-token-progress and --week-token-progress", result.Notices[0]);
        Assert.False(result.ToSave.HasLegacyLimits);
    }

    /// <summary>
    /// Verifies that the older-version notice names only the window that is still without a calibration.
    /// </summary>
    [Fact]
    public void Resolve_LegacyLimitsAndOneCalibrationGiven_NamesOnlyTheOtherWindow()
    {
        var saved = LimitsSettings.Empty with { SessionCostLimit = 12.5m };

        var result = LimitsStartup.Resolve(saved, 30, null, null, false, Used, Used, true);

        Assert.Single(result.Notices);
        Assert.Contains("--week-token-progress", result.Notices[0]);
        Assert.DoesNotContain("--session-token-progress", result.Notices[0]);
    }

    /// <summary>
    /// Verifies that no older-version notice is shown when both windows end up calibrated, and the legacy limits are still cleared.
    /// </summary>
    [Fact]
    public void Resolve_LegacyLimitsAndBothCalibrationsGiven_HasNoNoticeButClearsThem()
    {
        var saved = LimitsSettings.Empty with { SessionCostLimit = 12.5m, WeekCostLimit = 80m };

        var result = LimitsStartup.Resolve(saved, 30, 10, null, false, Used, Used, true);

        Assert.Empty(result.Notices);
        Assert.False(result.ToSave.HasLegacyLimits);
    }

    /// <summary>
    /// Verifies that a percentage given while nothing is counted keeps the saved calibration and says why.
    /// </summary>
    [Fact]
    public void Resolve_PercentageWhileNothingCounted_KeepsTheSavedCalibrationAndReportsIt()
    {
        var result = LimitsStartup.Resolve(WithCalibrations, 30, null, null, false, default, Used, true);

        Assert.Equal(WithCalibrations.SessionCalibration, result.SessionCalibration);
        Assert.Single(result.Notices);
        Assert.StartsWith("Session:", result.Notices[0]);
    }

    /// <summary>
    /// Verifies that an invalid percentage is an error and nothing is changed in the settings to save.
    /// </summary>
    [Fact]
    public void Resolve_InvalidPercentage_ReturnsAnErrorAndTheSettingsUnchanged()
    {
        var result = LimitsStartup.Resolve(WithCalibrations, double.NaN, null, null, false, Used, Used, true);

        Assert.NotNull(result.Error);
        Assert.Equal(WithCalibrations, result.ToSave);
    }

    /// <summary>
    /// Verifies that a calibration saved with an unusable percentage is dropped rather than used.
    /// </summary>
    [Fact]
    public void Resolve_SavedCalibrationWithUnusablePercent_IsDropped()
    {
        var saved = LimitsSettings.Empty with { SessionCalibration = new UsageCalibration(Used, 0), WeekCalibration = new UsageCalibration(Used, 10) };

        var result = LimitsStartup.Resolve(saved, null, null, null, false, Used, Used, true);

        Assert.Null(result.SessionCalibration);
        Assert.Equal(saved.WeekCalibration, result.WeekCalibration);
    }

    /// <summary>
    /// Verifies that a bad weekly percentage is found before the session window is resolved, so nothing is prompted for or
    /// noted about the session first.
    /// </summary>
    [Fact]
    public void Resolve_BadWeekPercentageAfterAGoodSessionOne_FailsBeforeResolvingTheSession()
    {
        // The good session percentage meets an empty window, which would add a notice if the session were resolved first.
        var result = LimitsStartup.Resolve(LimitsSettings.Empty, 30, double.NaN, null, false, default, Used, true);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Notices);
        Assert.Null(result.SessionCalibration);
        Assert.Equal(LimitsSettings.Empty, result.ToSave);
    }
}
