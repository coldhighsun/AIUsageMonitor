using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="OnlineLimitsApplier"/> and the note that says where the usage percentages come from.
/// </summary>
public sealed class OnlineLimitsApplierTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private static OnlineLimitsResult Live(OnlineQuota? session, OnlineQuota? week)
        => new(new(session, week), Now, false, OnlineLimitsFailure.None);

    private static OnlineLimitsResult Kept(OnlineQuota? session, OnlineQuota? week)
        => new(new(session, week), Now, true, OnlineLimitsFailure.RateLimited);

    /// <summary>
    /// A reset time that has not passed is used, live or kept; one that has passed, or none, is not.
    /// </summary>
    [Fact]
    public void SessionAnchor_UsesOnlyAReset_ThatHasNotPassed()
    {
        Assert.Equal(Now.AddHours(1), OnlineLimitsApplier.SessionAnchor(Live(new(5, Now.AddHours(1)), null), Now));
        Assert.Equal(Now.AddHours(1), OnlineLimitsApplier.SessionAnchor(Kept(new(5, Now.AddHours(1)), null), Now));
        Assert.Null(OnlineLimitsApplier.SessionAnchor(Live(new(5, Now.AddHours(-1)), null), Now));
        Assert.Null(OnlineLimitsApplier.SessionAnchor(Live(new(5, null), null), Now));
        Assert.Null(OnlineLimitsApplier.SessionAnchor(null, Now));
        Assert.Null(OnlineLimitsApplier.SessionAnchor(OnlineLimitsResult.Disabled, Now));
    }

    /// <summary>
    /// The weekly reset becomes a day and a local time rounded to the minute.
    /// </summary>
    [Fact]
    public void WeekAnchor_RoundsToTheMinuteInLocalTime()
    {
        var local = new DateTimeOffset(2026, 10, 26, 9, 59, 59, 700, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 26)));

        var anchor = OnlineLimitsApplier.WeekAnchor(Live(null, new(5, local)), Now);

        Assert.Equal(DayOfWeek.Monday, anchor!.Value.Day);
        Assert.Equal(new TimeSpan(10, 0, 0), anchor.Value.TimeOfDay);
        Assert.Null(OnlineLimitsApplier.WeekAnchor(Live(null, new(5, Now.AddDays(-1))), Now));
        Assert.Null(OnlineLimitsApplier.WeekAnchor(null, Now));
    }

    /// <summary>
    /// Only a live reading gives a percentage, and only one that can be calibrated against (not 0).
    /// </summary>
    [Fact]
    public void LivePercent_NeedsALiveReadingWithAUsableFigure()
    {
        Assert.Equal(12.5, OnlineLimitsApplier.LivePercent(Live(new(12.5, null), null), limits => limits.Session));
        Assert.Null(OnlineLimitsApplier.LivePercent(Kept(new(12.5, null), null), limits => limits.Session));
        Assert.Null(OnlineLimitsApplier.LivePercent(Live(new(0, null), null), limits => limits.Session));
        Assert.Null(OnlineLimitsApplier.LivePercent(Live(new(5, null), null), limits => limits.Week));
        Assert.Null(OnlineLimitsApplier.LivePercent(null, limits => limits.Session));
    }

    /// <summary>
    /// An exact percentage re-bases the calibration on the cost counted now; with nothing counted, or no percentage, it stays.
    /// </summary>
    [Fact]
    public void Recalibrate_RebasesOnlyWhenThereIsAPercentAndCostCounted()
    {
        var current = new UsageCalibration(new(1m, 1m, 0m, 0m), 10);
        var cost = new CostBreakdown(2m, 2m, 0m, 0m);

        var rebased = OnlineLimitsApplier.Recalibrate(current, 40, cost, 0.1m);

        Assert.Equal(new UsageCalibration(cost, 40), rebased);
        Assert.Equal(10m, rebased!.DeriveLimit(0.1m));
        Assert.Same(current, OnlineLimitsApplier.Recalibrate(current, null, cost, 0.1m));
        Assert.Same(current, OnlineLimitsApplier.Recalibrate(current, 40, default, 0.1m));
        Assert.Null(OnlineLimitsApplier.Recalibrate(null, null, cost, 0.1m));
    }

    /// <summary>
    /// A live reading is announced as exact, a missing one as an estimate with the reason, and the pause with its time.
    /// </summary>
    [Fact]
    public void FormatOnlineNote_SaysWhereThePercentagesComeFrom()
    {
        Assert.Null(SpectreRenderer.FormatOnlineNote(null, Now));
        Assert.Contains("exact", SpectreRenderer.FormatOnlineNote(Live(new(5, null), null), Now));
        Assert.Contains("--offline", SpectreRenderer.FormatOnlineNote(OnlineLimitsResult.Disabled, Now));
        Assert.Contains("sign-in was not found", SpectreRenderer.FormatOnlineNote(new(null, null, false, OnlineLimitsFailure.NotSignedIn), Now));
        Assert.Contains("rejected", SpectreRenderer.FormatOnlineNote(new(null, null, false, OnlineLimitsFailure.Rejected), Now));
        Assert.Contains("could not be reached", SpectreRenderer.FormatOnlineNote(new(null, null, false, OnlineLimitsFailure.Unavailable), Now));
        Assert.Contains("pause until", SpectreRenderer.FormatOnlineNote(
            new(null, null, false, OnlineLimitsFailure.RateLimited, Now.AddMinutes(10)), Now));
        Assert.Contains("estimate", SpectreRenderer.FormatOnlineNote(Kept(new(5, null), null), Now));
    }

    /// <summary>
    /// A desktop sample is shown beside the estimate, with its time and age, and does not drive the bar.
    /// </summary>
    [Fact]
    public void FormatOnlineNote_ShowsTheDesktopSampleAsAnEstimate()
    {
        var sample = new OnlineLimitsResult(
            new(new(35.5, null), new(45, null)), Now.AddHours(-2), true, OnlineLimitsFailure.RateLimited, Now.AddMinutes(30), FromDesktopApp: true);

        var note = SpectreRenderer.FormatOnlineNote(sample, Now)!;

        Assert.Contains("estimate", note);
        Assert.Contains("Claude desktop app", note);
        Assert.Contains("session 35.5%", note);
        Assert.Contains("week 45%", note);
        Assert.Contains("2h 0m ago", note);
        Assert.Null(OnlineLimitsApplier.LivePercent(sample, limits => limits.Week));

        var offline = sample with { Failure = OnlineLimitsFailure.Disabled, RetryAt = null };
        Assert.Contains("--offline", SpectreRenderer.FormatOnlineNote(offline, Now));
        Assert.Contains("week 45%", SpectreRenderer.FormatOnlineNote(offline, Now));
    }

    /// <summary>
    /// The exact percentage drives the usage bar of its window even with no calibration, and the estimate stays for the other.
    /// </summary>
    [Fact]
    public void BuildUsageLimits_LiveReading_ShowsTheExactPercent()
    {
        var window = new UsageWindowSummary(
            Now.AddHours(-2), Now.AddHours(3), 5, 1000, [], WindowConfidence.Confirmed, new(1m, 1m, 0m, 0m));
        var rendered = Render(SpectreRenderer.BuildUsageLimits(
            window, window, null, null, false, true, 0.1m, Live(new(42, Now.AddHours(3)), null)));

        Assert.Contains("42%", rendered);
        Assert.Contains("exact", rendered);
    }

    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        using var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new() { Out = new Spectre.Console.AnsiConsoleOutput(writer), Ansi = Spectre.Console.AnsiSupport.No, ColorSystem = Spectre.Console.ColorSystemSupport.NoColors });
        console.Profile.Width = 250;
        console.Write(renderable);
        return writer.ToString();
    }
}
