using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using System.Globalization;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="SpectreRenderer"/>.
/// </summary>
public class SpectreRendererTests
{
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
