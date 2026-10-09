using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for the date-range resolver, option parsing, model filter, JSON shapes and export format validation of the report commands.
/// </summary>
public class ReportOptionsTests
{
    /// <summary>
    /// The fixed "today" used by the date-range tests.
    /// </summary>
    private static readonly DateOnly Today = new(2026, 10, 9);

    /// <summary>
    /// Resolves a request against <see cref="Today"/> with a 7-day default range.
    /// </summary>
    /// <param name="request">The raw arguments.</param>
    /// <returns>The resolution.</returns>
    private static DateRangeResolution Resolve(DateRangeRequest request)
    {
        return DateRangeResolver.Resolve(request, Today, TimeSpan.FromDays(7));
    }

    /// <summary>
    /// Verifies that with no arguments the range is the default length ending today.
    /// </summary>
    [Fact]
    public void Resolve_NoArguments_ReturnsDefaultRangeEndingToday()
    {
        var resolution = Resolve(new DateRangeRequest());

        Assert.Null(resolution.Error);
        Assert.Equal(new DateRange(new DateOnly(2026, 10, 3), Today), resolution.Range);
    }

    /// <summary>
    /// Verifies that --date selects exactly that day.
    /// </summary>
    [Fact]
    public void Resolve_Date_ReturnsThatSingleDay()
    {
        var resolution = Resolve(new DateRangeRequest(Date: "2026-09-30"));

        Assert.Equal(new DateRange(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30)), resolution.Range);
    }

    /// <summary>
    /// Verifies that --from without --to runs through today.
    /// </summary>
    [Fact]
    public void Resolve_FromOnly_EndsToday()
    {
        var resolution = Resolve(new DateRangeRequest(From: "2026-10-01"));

        Assert.Equal(new DateRange(new DateOnly(2026, 10, 1), Today), resolution.Range);
    }

    /// <summary>
    /// Verifies that --from and --to give an explicit inclusive range.
    /// </summary>
    [Fact]
    public void Resolve_FromAndTo_ReturnsExplicitRange()
    {
        var resolution = Resolve(new DateRangeRequest(From: "2026-08-01", To: "2026-08-15"));

        Assert.Equal(new DateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)), resolution.Range);
    }

    /// <summary>
    /// Verifies that --last N covers N days including today.
    /// </summary>
    [Fact]
    public void Resolve_Last_ReturnsNDaysEndingToday()
    {
        var resolution = Resolve(new DateRangeRequest(Last: 3));

        Assert.Equal(new DateRange(new DateOnly(2026, 10, 7), Today), resolution.Range);
    }

    /// <summary>
    /// Verifies that the longest allowed --last is accepted.
    /// </summary>
    [Fact]
    public void Resolve_LastAtMaximum_IsAccepted()
    {
        var resolution = Resolve(new DateRangeRequest(Last: DateRangeResolver.MaxDays));

        Assert.NotNull(resolution.Range);
    }

    /// <summary>
    /// Verifies that --month covers the whole calendar month, including a leap-year February.
    /// </summary>
    [Theory]
    [InlineData("2026-09", "2026-09-01", "2026-09-30")]
    [InlineData("2028-02", "2028-02-01", "2028-02-29")]
    [InlineData("2999-12", "2999-12-01", "2999-12-31")]
    public void Resolve_Month_ReturnsWholeCalendarMonth(string month, string expectedFrom, string expectedTo)
    {
        var resolution = Resolve(new DateRangeRequest(Month: month));

        Assert.Equal(new DateRange(DateOnly.Parse(expectedFrom), DateOnly.Parse(expectedTo)), resolution.Range);
    }

    /// <summary>
    /// Verifies that invalid or contradictory arguments are rejected with a message instead of throwing.
    /// </summary>
    [Theory]
    [InlineData("2026-10-01", null, null, 2, null, "only one")]
    [InlineData(null, "2026-10-01", null, 3, null, "only one")]
    [InlineData(null, null, null, 3, "2026-09", "only one")]
    [InlineData("10/01/2026", null, null, null, null, "--date")]
    [InlineData(null, "nope", null, null, null, "--from")]
    [InlineData(null, "2026-10-01", "2026-13-40", null, null, "--to")]
    [InlineData(null, null, "2026-10-01", null, null, "requires --from")]
    [InlineData(null, "2026-10-05", "2026-10-01", null, null, "must not be after")]
    [InlineData(null, null, null, 0, null, "at least 1")]
    [InlineData(null, null, null, -5, null, "at least 1")]
    [InlineData(null, null, null, 3651, null, "at most 3650")]
    [InlineData(null, null, null, null, "2026-13", "--month")]
    [InlineData(null, null, null, null, "9999-12", "between 2000 and 2999")]
    [InlineData(null, null, null, null, "1999-12", "between 2000 and 2999")]
    [InlineData(null, "2026-01-01", "9999-12-31", null, null, "between 2000 and 2999")]
    [InlineData(null, "0001-01-01", null, null, null, "between 2000 and 2999")]
    [InlineData("9999-12-31", null, null, null, null, "between 2000 and 2999")]
    [InlineData(null, "2000-01-01", "2026-10-09", null, null, "longer than 3650 days")]
    [InlineData(null, null, null, 2147483647, null, "got 2147483647")]
    public void Resolve_InvalidRequest_ReturnsError(string? date, string? from, string? to, int? lastDays, string? month, string expectedError)
    {
        var resolution = Resolve(new DateRangeRequest(date, from, to, lastDays, month));

        Assert.Null(resolution.Range);
        Assert.Contains(expectedError, resolution.Error);
    }

    /// <summary>
    /// Verifies that a range spanning exactly the maximum number of days is accepted.
    /// </summary>
    [Fact]
    public void Resolve_FromToAtMaximumSpan_IsAccepted()
    {
        var resolution = Resolve(new DateRangeRequest(From: "2016-10-12", To: "2026-10-09"));

        Assert.Equal(DateRangeResolver.MaxDays, resolution.Range!.Value.To.DayNumber - resolution.Range.Value.From.DayNumber + 1);
    }

    /// <summary>
    /// Verifies that the update check runs after ordinary commands but not for watch or --json runs.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void ShouldCheckAfterCommand_WatchAndJson_SkipsTheCheck(bool isWatch, bool jsonRequested, bool expected)
    {
        Assert.Equal(expected, UpdateNotice.ShouldCheckAfterCommand(isWatch, jsonRequested));
    }

    /// <summary>
    /// Verifies that the week command parses the range options and that --month is only offered by the month command.
    /// </summary>
    [Fact]
    public void Create_WeekAndMonthCommands_ExposeTheirOwnRangeOptions()
    {
        var week = WeekCommand.Create(null!);
        var month = MonthCommand.Create(null!);

        Assert.Empty(week.Parse("--from 2026-10-01 --to 2026-10-05 --json").Errors);
        Assert.Empty(week.Parse("--last 3").Errors);
        Assert.NotEmpty(week.Parse("--month 2026-09").Errors);
        Assert.Empty(month.Parse("--month 2026-09").Errors);
        Assert.NotEmpty(week.Parse("--last abc").Errors);
    }

    /// <summary>
    /// Verifies that the today command offers --date but not a range.
    /// </summary>
    [Fact]
    public void Create_TodayCommand_ExposesDateAndJsonOnly()
    {
        var today = TodayCommand.Create(null!);

        Assert.Empty(today.Parse("--date 2026-10-01 --json").Errors);
        Assert.NotEmpty(today.Parse("--from 2026-10-01").Errors);
    }

    /// <summary>
    /// Verifies that --json is detected on the invoked command only when it was actually passed.
    /// </summary>
    [Fact]
    public void IsRequested_JsonFlag_ReflectsWhetherItWasPassed()
    {
        var models = ModelsCommand.Create(null!);
        var export = ExportCommand.Create(null!);

        Assert.True(JsonOutput.IsRequested(models.Parse("--json")));
        Assert.False(JsonOutput.IsRequested(models.Parse([])));
        Assert.False(JsonOutput.IsRequested(export.Parse([])));
    }

    /// <summary>
    /// Verifies that the model filter matches case-insensitively on a substring and keeps the original order.
    /// </summary>
    [Fact]
    public void FilterByModel_Substring_KeepsMatchingModelsOnly()
    {
        List<ModelDistribution> models =
        [
            new("claude-opus-5-5", 1, 1, 1, 1, 4, 40, 1m),
            new("claude-sonnet-5-5", 1, 1, 1, 1, 4, 40, 1m),
            new("claude-haiku-5-5", 1, 1, 1, 1, 2, 20, 1m),
        ];

        var filtered = ModelsCommand.FilterByModel(models, " SONNET ");

        Assert.Equal("claude-sonnet-5-5", Assert.Single(filtered).ModelName);
    }

    /// <summary>
    /// Verifies that a missing or blank filter leaves the list untouched.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FilterByModel_NoFilter_ReturnsAllModels(string? filter)
    {
        List<ModelDistribution> models = [new("a", 1, 1, 1, 1, 4, 100, 1m)];

        Assert.Same(models, ModelsCommand.FilterByModel(models, filter));
    }

    /// <summary>
    /// Verifies that JSON output uses camelCase names and the expected values.
    /// </summary>
    [Fact]
    public void Serialize_DailySummary_UsesCamelCaseProperties()
    {
        var summary = new DailySummary(new DateOnly(2026, 10, 9), 5, 2, 3, 1234, new() { ["opus"] = 1234 }, 1.5m);

        var json = JsonOutput.Serialize(summary, CliJsonContext.Default.DailySummary);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("2026-10-09", root.GetProperty("date").GetString());
        Assert.Equal(1234, root.GetProperty("totalTokens").GetInt64());
        Assert.Equal(1.5m, root.GetProperty("estimatedCost").GetDecimal());
        Assert.Equal(1234, root.GetProperty("tokensByModel").GetProperty("opus").GetInt64());
    }

    /// <summary>
    /// Verifies that session durations are emitted as plain seconds, including past 24 hours.
    /// </summary>
    [Fact]
    public void Serialize_SessionStats_EmitsDurationsAsSeconds()
    {
        var stats = new SessionStats(3, TimeSpan.FromMinutes(5), 2.5, TimeSpan.FromHours(30), "abc");

        var json = JsonOutput.Serialize(SessionStatsReport.Create(stats), CliJsonContext.Default.SessionStatsReport);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(300, root.GetProperty("avgDurationSeconds").GetDouble());
        Assert.Equal(108000, root.GetProperty("longestDurationSeconds").GetDouble());
        Assert.Equal("abc", root.GetProperty("longestSessionId").GetString());
    }

    /// <summary>
    /// Verifies that the model and hourly reports serialize with camelCase names.
    /// </summary>
    [Fact]
    public void Serialize_ModelAndHourLists_ProduceJson()
    {
        var models = JsonOutput.Serialize<List<ModelDistribution>>([new("m", 1, 2, 3, 4, 10, 100, 0.5m)], CliJsonContext.Default.ListModelDistribution);
        var hours = JsonOutput.Serialize<List<HourlyActivity>>([new(9, 100)], CliJsonContext.Default.ListHourlyActivity);

        using var modelDocument = JsonDocument.Parse(models);
        using var hourDocument = JsonDocument.Parse(hours);
        Assert.Equal("m", modelDocument.RootElement[0].GetProperty("modelName").GetString());
        Assert.Equal(9, hourDocument.RootElement[0].GetProperty("hour").GetInt32());
    }
}
