using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Globalization;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="SpectreRenderer"/>.
/// </summary>
public class SpectreRendererTests
{
    /// <summary>
    /// Renders a renderable to plain text on a wide console, so nothing is wrapped or truncated.
    /// </summary>
    /// <param name="renderable">The renderable to draw.</param>
    /// <returns>The text drawn.</returns>
    private static string RenderToText(IRenderable renderable)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = 250;
        console.Write(renderable);

        return writer.ToString();
    }

    /// <summary>
    /// A window with the given cost, five hours long and one hour in, so its time progress is 20%.
    /// </summary>
    /// <param name="breakdown">The cost of the window by kind of token.</param>
    /// <returns>The window.</returns>
    private static UsageWindowSummary SessionWith(CostBreakdown breakdown)
    {
        var now = DateTimeOffset.Now;

        return new UsageWindowSummary(now.AddHours(-1), now.AddHours(4), 1, 100, [], WindowConfidence.Confirmed, breakdown);
    }

    /// <summary>
    /// A weekly window that is unknown, so it adds nothing to the text being checked.
    /// </summary>
    private static readonly UsageWindowSummary UnknownWeek = new(null, null, 0, 0, [], WindowConfidence.Unknown, default);

    /// <summary>
    /// Verifies that the usage-progress bar measures the cache-read-weighted cost against the limit derived from the
    /// calibration, not the plain list-price cost.
    /// </summary>
    [Fact]
    public void BuildUsageLimits_WeightedCost_FillsTheBarWithTheWeightedShareOfTheLimit()
    {
        // Weighted under 0.1: 1 + 2 + 1 + 3 = 7 of the 16 list-price dollars. The reading says 25% at that same cost, so
        // the limit is 28 and the bar must show 25%, while the list-price cost would give 57%.
        var breakdown = new CostBreakdown(Input: 1m, Output: 2m, CacheRead: 10m, CacheWrite: 3m);
        var calibration = new UsageCalibration(breakdown, 25);

        var text = RenderToText(SpectreRenderer.BuildUsageLimits(
            SessionWith(breakdown), UnknownWeek, calibration, null, recalibrationAvailable: false, cacheReadWeight: 0.1m));

        Assert.Contains("25%", text);
        Assert.DoesNotContain("57%", text);
    }

    /// <summary>
    /// Verifies that the bar still shows the percentage that was read when the window has the cost it had at the reading,
    /// whichever weight is in effect, because the limit is derived again under that weight.
    /// </summary>
    /// <param name="weight">The cache read weight.</param>
    [Theory]
    [InlineData(0.1)]
    [InlineData(1)]
    public void BuildUsageLimits_CostAtTheReading_ShowsThePercentageReadUnderAnyWeight(double weight)
    {
        var breakdown = new CostBreakdown(Input: 1m, Output: 2m, CacheRead: 10m, CacheWrite: 3m);
        var calibration = new UsageCalibration(breakdown, 40);

        var text = RenderToText(SpectreRenderer.BuildUsageLimits(
            SessionWith(breakdown), UnknownWeek, calibration, null, recalibrationAvailable: false, cacheReadWeight: (decimal)weight));

        Assert.Contains("40%", text);
    }

    /// <summary>
    /// Verifies that the bar is left blank when the calibration gives no limit under the weight in effect.
    /// </summary>
    [Fact]
    public void BuildUsageLimits_CalibrationWithoutLimit_LeavesTheBarBlank()
    {
        var onlyCacheReads = new CostBreakdown(0m, 0m, 5m, 0m);

        var text = RenderToText(SpectreRenderer.BuildUsageLimits(
            SessionWith(onlyCacheReads), UnknownWeek, new UsageCalibration(onlyCacheReads, 40), null, recalibrationAvailable: false, cacheReadWeight: 0m));

        Assert.DoesNotContain("40%", text);
    }

    /// <summary>
    /// Verifies that the notices are drawn in the view, in yellow text, between the content and the update notice.
    /// </summary>
    [Fact]
    public void WithNotices_NoticesAndUpdateNotice_DrawsThemInOrderAfterTheContent()
    {
        var text = RenderToText(SpectreRenderer.WithNotices(
            new Markup("CONTENT"), ["first notice", "second [notice]"], new Markup("UPDATE"), new Markup("FOOTER")));

        var order = new[] { "CONTENT", "first notice", "second [notice]", "UPDATE", "FOOTER" }.Select(part => text.IndexOf(part, StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.OrderBy(index => index), order);
    }

    /// <summary>
    /// Verifies that with nothing to add the content itself is returned.
    /// </summary>
    [Fact]
    public void WithNotices_NothingToAdd_ReturnsTheContent()
    {
        var content = new Markup("CONTENT");

        var result = SpectreRenderer.WithNotices(content, [], null, null);

        Assert.Same(content, result);
    }

    /// <summary>
    /// Verifies that the usage split names each kind of token with its share of the weighted cost, in any culture.
    /// </summary>
    [Fact]
    public void FormatCostSplit_WindowWithUsage_ShowsTheShareOfEachKindInvariantly()
    {
        var original = CultureInfo.CurrentCulture;
        var window = new UsageWindowSummary(
            DateTimeOffset.Now, DateTimeOffset.Now.AddHours(1), 1, 100, [], WindowConfidence.Confirmed,
            new CostBreakdown(Input: 1m, Output: 2m, CacheRead: 10m, CacheWrite: 3m));

        string? text;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            text = SpectreRenderer.FormatCostSplit("Session", window, 0.1m);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(
            "[grey]Session usage split: cache read 14% · output 29% · cache write 43% · input 14% (the bar counts $7.00 of the $16.00 list-price cost, cache reads at 0.1 of list price)[/]",
            text);
    }

    /// <summary>
    /// Verifies that a window without usage, or without a start, has no split line.
    /// </summary>
    [Fact]
    public void FormatCostSplit_NoUsageOrNoWindow_ReturnsNull()
    {
        var empty = new UsageWindowSummary(DateTimeOffset.Now, null, 0, 0, [], WindowConfidence.Confirmed, default);
        var unknown = new UsageWindowSummary(null, null, 0, 0, [], WindowConfidence.Unknown, default);

        Assert.Null(SpectreRenderer.FormatCostSplit("Session", empty, 0.1m));
        Assert.Null(SpectreRenderer.FormatCostSplit("Session", unknown, 0.1m));
    }

    /// <summary>
    /// Verifies that cost is always shown in US dollars, whatever the current culture's currency is.
    /// </summary>
    /// <param name="culture">The culture to format under.</param>
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void FormatCost_AnyCulture_UsesDollarSignAndInvariantSeparators(string culture)
    {
        var original = CultureInfo.CurrentCulture;

        string text;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            text = SpectreRenderer.FormatCost(1234.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal("$1,234.50", text);
    }

    /// <summary>
    /// Verifies that the cost column of the daily summary uses the dollar sign rather than the culture's currency.
    /// </summary>
    [Fact]
    public void BuildDailySummary_ChineseCulture_ShowsCostInDollars()
    {
        var original = CultureInfo.CurrentCulture;
        var summary = new DailySummary(new(2026, 8, 1), 1, 1, 0, 100, [], 12.34m);

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("zh-CN");
            rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildDailySummary(summary));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Contains("$12.34", rendered);
        Assert.DoesNotContain("¥", rendered);
    }

    /// <summary>
    /// Verifies that tokens, percentages and cost in one table all use the same invariant number format
    /// under a culture with comma decimals.
    /// </summary>
    [Fact]
    public void BuildModelDistribution_CommaDecimalCulture_UsesInvariantNumbers()
    {
        List<ModelDistribution> models = [new("sonnet-5", 1_500_000, 2_500, 0, 0, 1_502_500, 12.5, 1234.5m)];
        var original = CultureInfo.CurrentCulture;

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildModelDistribution(models));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Contains("1.50M", rendered);
        Assert.Contains("2.5K", rendered);
        Assert.Contains("12.5%", rendered);
        Assert.Contains("$1,234.50", rendered);
    }

    /// <summary>
    /// Verifies that counts use comma thousands separators regardless of the current culture.
    /// </summary>
    [Fact]
    public void BuildDailySummary_CommaDecimalCulture_UsesInvariantCounts()
    {
        var summary = new DailySummary(new(2026, 8, 1), 12_345, 1, 0, 100, [], 1m);
        var original = CultureInfo.CurrentCulture;

        string rendered;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildDailySummary(summary));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Contains("12,345", rendered);
    }

    /// <summary>
    /// Verifies that a model name containing square brackets is shown literally in the model table instead of failing to render.
    /// </summary>
    [Fact]
    public void BuildModelDistribution_ModelNameWithBrackets_RendersNameVerbatim()
    {
        List<ModelDistribution> models = [new("custom[beta]", 1, 2, 3, 4, 10, 100, 1m)];

        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildModelDistribution(models));

        Assert.Contains("custom[beta]", rendered);
    }

    /// <summary>
    /// Verifies that a model name containing square brackets does not break the per-model bar chart.
    /// </summary>
    [Fact]
    public void BuildDailySummary_ModelNameWithBrackets_RendersChartLabelVerbatim()
    {
        var summary = new DailySummary(new(2026, 8, 1), 1, 1, 0, 100, new() { ["custom[beta]"] = 100 }, 1m);

        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildDailySummary(summary));

        Assert.Contains("custom[beta]", rendered);
    }

    /// <summary>
    /// Verifies that a session id containing square brackets is shown literally.
    /// </summary>
    [Fact]
    public void BuildSessionStats_SessionIdWithBrackets_RendersIdVerbatim()
    {
        var stats = new SessionStats(1, TimeSpan.Zero, 2.0, TimeSpan.FromMinutes(5), "id[1]");

        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionStats(stats));

        Assert.Contains("id[1]", rendered);
    }

    /// <summary>
    /// Verifies that an empty hourly trend renders a note instead of an empty chart, which would throw when rendered.
    /// </summary>
    [Fact]
    public void BuildHourlyTokenChart_NoBuckets_RendersNoteInsteadOfThrowing()
    {
        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildHourlyTokenChart([]));

        Assert.Contains("No hourly activity yet.", rendered);
    }

    /// <summary>
    /// Verifies that hourly buckets are rendered as a chart.
    /// </summary>
    [Fact]
    public void BuildHourlyTokenChart_WithBuckets_RendersTheChart()
    {
        var rendered = ConsoleMarkupTests.RenderPlain(
            SpectreRenderer.BuildHourlyTokenChart([new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), 3, 1500)]));

        Assert.Contains("Tokens by Hour", rendered);
        Assert.Contains("09:00", rendered);
    }
}
