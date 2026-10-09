using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude.Models;
using System.CommandLine;
using System.Globalization;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for the CSV output and the format validation of <see cref="ExportCommand"/>.
/// </summary>
public class ExportCommandTests
{
    /// <summary>
    /// Verifies that decimal numbers use a period regardless of the current culture, so a comma-decimal culture cannot add columns.
    /// </summary>
    [Fact]
    public void ExportCsv_CommaDecimalCulture_UsesInvariantNumbers()
    {
        var cache = new StatsCache
        {
            DailyActivity = [new() { Date = new(2026, 8, 1), MessageCount = 3, SessionCount = 1, ToolCallCount = 2 }],
        };
        List<ModelDistribution> models = [new("sonnet-5", 1000, 2000, 3000, 4000, 10000, 12.5, 1.5m)];
        var original = CultureInfo.CurrentCulture;

        string csv;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            csv = ExportCommand.ExportCsv(cache, models);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Contains("2026-08-01,3,1,2", csv);
        Assert.Contains("sonnet-5,1000,2000,3000,4000,10000,12.5,1.50", csv);
    }

    /// <summary>
    /// Verifies that a model name containing a comma or quote is quoted so the row keeps its column count.
    /// </summary>
    [Fact]
    public void ExportCsv_ModelNameWithCommaAndQuote_QuotesTheField()
    {
        var cache = new StatsCache();
        List<ModelDistribution> models = [new("odd,\"name\"", 1, 2, 3, 4, 10, 100, 0m)];

        var csv = ExportCommand.ExportCsv(cache, models);

        Assert.Contains("\"odd,\"\"name\"\"\",1,2,3,4,10,100.0,0.00", csv);
    }

    /// <summary>
    /// Verifies that the export command rejects an unknown format and accepts json and csv in any case.
    /// </summary>
    [Theory]
    [InlineData("xml", false)]
    [InlineData("", false)]
    [InlineData("json", true)]
    [InlineData("CSV", true)]
    public void Create_FormatOption_ValidatesFormat(string format, bool valid)
    {
        var export = ExportCommand.Create(null!);

        var errors = export.Parse(["--format", format]).Errors;

        Assert.Equal(valid, errors.Count == 0);
    }
}
