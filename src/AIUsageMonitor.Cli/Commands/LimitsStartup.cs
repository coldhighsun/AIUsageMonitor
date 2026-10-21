using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Works out, at the start of <c>watch --view limits</c>, which cache read weight and usage calibrations apply and what
/// to save, without reading the settings file or the terminal's state so that it can be tested. The only console
/// interaction left is the prompt for a missing percentage, which is skipped when the caller says there is no interactive
/// terminal.
/// </summary>
internal static class LimitsStartup
{
    /// <summary>
    /// The result of resolving the limits at startup.
    /// </summary>
    /// <param name="CacheReadWeight">The cache read weight in effect.</param>
    /// <param name="SessionCalibration">The session usage calibration in effect, if any.</param>
    /// <param name="WeekCalibration">The weekly usage calibration in effect, if any.</param>
    /// <param name="ToSave">The settings to persist.</param>
    /// <param name="Notices">Plain-text notices for the user to read in the view.</param>
    /// <param name="Error">An error message if an argument was invalid; the other values are not meaningful then.</param>
    internal sealed record Result(
        decimal CacheReadWeight,
        UsageCalibration? SessionCalibration,
        UsageCalibration? WeekCalibration,
        LimitsSettings ToSave,
        IReadOnlyList<string> Notices,
        string? Error);

    /// <summary>
    /// Resolves the weight and the calibrations from the arguments, the saved settings and the cost of the two windows.
    /// A usage percentage that is given prompts for nothing; one that is missing is asked for unless the input is redirected.
    /// </summary>
    /// <param name="saved">The settings as saved.</param>
    /// <param name="sessionPercentArg">The <c>--session-token-progress</c> argument, if given.</param>
    /// <param name="weekPercentArg">The <c>--week-token-progress</c> argument, if given.</param>
    /// <param name="weightArg">The <c>--cache-read-weight</c> argument, if given.</param>
    /// <param name="resetWeight">Whether <c>--reset-cache-read-weight</c> was given.</param>
    /// <param name="sessionCost">The estimated cost of the session window so far, by kind of token.</param>
    /// <param name="weekCost">The estimated cost of the weekly window so far, by kind of token.</param>
    /// <param name="inputRedirected">Whether there is no interactive terminal, so that nothing can be prompted for.</param>
    /// <returns>What applies and what to save, or an error.</returns>
    internal static Result Resolve(
        LimitsSettings saved, double? sessionPercentArg, double? weekPercentArg, decimal? weightArg, bool resetWeight,
        CostBreakdown sessionCost, CostBreakdown weekCost, bool inputRedirected)
    {
        var weight = QuotaCost.ResolveWeight(weightArg, saved.CacheReadWeight, resetWeight);
        var notices = new List<string>();

        // Both arguments are checked before the first window resolves, so that a bad one is not found after the user was
        // already prompted for the other.
        var argumentError = LimitsAnchors.ValidatePercentArgument(sessionPercentArg) ?? LimitsAnchors.ValidatePercentArgument(weekPercentArg);
        if (argumentError is not null)
        {
            return new(weight, null, null, saved, notices, argumentError);
        }

        if (!LimitsAnchors.ResolveCalibration(
                sessionPercentArg, UsageCalibration.OrNullIfInvalid(saved.SessionCalibration), sessionCost, weight,
                "Session", notices, inputRedirected, out var sessionCalibration, out var error)
            || !LimitsAnchors.ResolveCalibration(
                weekPercentArg, UsageCalibration.OrNullIfInvalid(saved.WeekCalibration), weekCost, weight,
                "Weekly", notices, inputRedirected, out var weekCalibration, out error))
        {
            return new(weight, null, null, saved, notices, error);
        }

        AddLegacyLimitsNotice(saved, sessionCalibration, weekCalibration, inputRedirected, notices);

        var toSave = saved with
        {
            SessionCalibration = sessionCalibration,
            WeekCalibration = weekCalibration,
            CacheReadWeight = QuotaCost.WeightToSave(weightArg, saved.CacheReadWeight, resetWeight),
            SessionCostLimit = null,
            WeekCostLimit = null,
        };

        return new(weight, sessionCalibration, weekCalibration, toSave, notices, null);
    }

    /// <summary>
    /// Tells the user that limits saved by an older version were not carried over, when that leaves a window without a
    /// calibration and nothing was prompted for. It is told once: the legacy limits are cleared in the settings to save.
    /// </summary>
    /// <param name="saved">The settings as saved.</param>
    /// <param name="sessionCalibration">The session calibration that resulted, if any.</param>
    /// <param name="weekCalibration">The weekly calibration that resulted, if any.</param>
    /// <param name="inputRedirected">Whether there is no interactive terminal; with one, the user was just asked.</param>
    /// <param name="notices">Receives the notice.</param>
    private static void AddLegacyLimitsNotice(
        LimitsSettings saved, UsageCalibration? sessionCalibration, UsageCalibration? weekCalibration,
        bool inputRedirected, ICollection<string> notices)
    {
        if (!saved.HasLegacyLimits || !inputRedirected)
        {
            return;
        }

        var missing = new List<string>();
        if (sessionCalibration is null)
        {
            missing.Add("--session-token-progress");
        }

        if (weekCalibration is null)
        {
            missing.Add("--week-token-progress");
        }

        if (missing.Count > 0)
        {
            notices.Add($"The usage limits saved by an older version cannot be carried over; pass {string.Join(" and ", missing)} to set them again.");
        }
    }
}
