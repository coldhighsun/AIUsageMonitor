using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Services;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Runs the date-range, model, hour and session-statistics report commands end to end against a fake provider and checks what
/// they print and return.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CommandActionTests : CommandActionTestBase
{
    /// <summary>
    /// Verifies that today --json prints the summary for the clock's date and nothing else.
    /// </summary>
    [Fact]
    public void Create_TodayJson_PrintsSummaryForClockDate()
    {
        var result = Run(TodayCommand.Create(DataService, Clock), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-10-09", document.RootElement.GetProperty("date").GetString());
        Assert.Equal(10, document.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal("", result.Error);
    }

    /// <summary>
    /// Verifies that --date picks another day, and that a day without activity yields an empty summary.
    /// </summary>
    [Fact]
    public void Create_TodayJsonWithDate_PrintsThatDayOrAnEmptySummary()
    {
        var yesterday = Run(TodayCommand.Create(DataService, Clock), "--date", "2026-10-08", "--json");
        var quiet = Run(TodayCommand.Create(DataService, Clock), "--date", "2026-01-01", "--json");

        using var yesterdayDocument = JsonDocument.Parse(yesterday.Out);
        using var quietDocument = JsonDocument.Parse(quiet.Out);
        Assert.Equal(20, yesterdayDocument.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal(0, quietDocument.RootElement.GetProperty("messages").GetInt32());
        Assert.Equal("2026-01-01", quietDocument.RootElement.GetProperty("date").GetString());
    }

    /// <summary>
    /// Verifies that an invalid date is reported on stderr with exit code 1 and nothing on stdout.
    /// </summary>
    [Fact]
    public void Create_TodayInvalidDate_WritesErrorToStderrAndReturnsOne()
    {
        var result = Run(TodayCommand.Create(DataService, Clock), "--date", "2026-13-01", "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--date", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that week --last counts back from the clock's date.
    /// </summary>
    [Fact]
    public void Create_WeekJsonWithLast_CoversTheRequestedDays()
    {
        var result = Run(WeekCommand.Create(DataService, Clock), "--last", "2", "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-10-08", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-10-09", document.RootElement.GetProperty("to").GetString());
        Assert.Equal(30, document.RootElement.GetProperty("totalMessages").GetInt32());
    }

    /// <summary>
    /// Verifies that the default ranges of week and month are 7 and 30 days.
    /// </summary>
    [Fact]
    public void Create_WeekAndMonthJsonWithoutRange_UseTheirDefaultLength()
    {
        var week = Run(WeekCommand.Create(DataService, Clock), "--json");
        var month = Run(MonthCommand.Create(DataService, Clock), "--json");

        using var weekDocument = JsonDocument.Parse(week.Out);
        using var monthDocument = JsonDocument.Parse(month.Out);
        Assert.Equal("2026-10-03", weekDocument.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-09-10", monthDocument.RootElement.GetProperty("from").GetString());
    }

    /// <summary>
    /// Verifies that month --month covers the whole calendar month.
    /// </summary>
    [Fact]
    public void Create_MonthJsonWithMonth_CoversTheCalendarMonth()
    {
        var result = Run(MonthCommand.Create(DataService, Clock), "--month", "2026-09", "--json");

        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-09-01", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-09-30", document.RootElement.GetProperty("to").GetString());
    }

    /// <summary>
    /// Verifies that absurd ranges are rejected with exit code 1 rather than crashing.
    /// </summary>
    [Theory]
    [InlineData("week", "--last 2147483647", "at most")]
    [InlineData("week", "--last 0", "at least 1")]
    [InlineData("month", "--month 9999-12", "between 2000 and 2999")]
    [InlineData("week", "--from 2026-10-05 --to 2026-10-01", "must not be after")]
    public void Create_PeriodCommandsInvalidRange_ReturnOneWithMessage(string commandName, string options, string expectedError)
    {
        var command = commandName == "week" ? WeekCommand.Create(DataService, Clock) : MonthCommand.Create(DataService, Clock);

        var result = Run(command, [.. options.Split(' '), "--json"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that models --json honors --model and returns an empty array when nothing matches.
    /// </summary>
    [Fact]
    public void Create_ModelsJson_FiltersByModelName()
    {
        var all = Run(ModelsCommand.Create(DataService), "--json");
        var sonnet = Run(ModelsCommand.Create(DataService), "--model", "SONNET", "--json");
        var none = Run(ModelsCommand.Create(DataService), "--model", "gpt", "--json");

        using var allDocument = JsonDocument.Parse(all.Out);
        using var sonnetDocument = JsonDocument.Parse(sonnet.Out);
        using var noneDocument = JsonDocument.Parse(none.Out);
        Assert.Equal(2, allDocument.RootElement.GetArrayLength());
        Assert.Equal("claude-sonnet-5-5", Assert.Single(sonnetDocument.RootElement.EnumerateArray()).GetProperty("modelName").GetString());
        Assert.Equal(0, noneDocument.RootElement.GetArrayLength());
    }

    /// <summary>
    /// Verifies that a filter matching nothing prints a hint on stderr instead of an empty table.
    /// </summary>
    [Fact]
    public void Create_ModelsFilterMatchesNothing_WritesHintToStderr()
    {
        var result = Run(ModelsCommand.Create(DataService), "--model", "gpt");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("No model matches 'gpt'", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that sessions --json reports durations in seconds.
    /// </summary>
    [Fact]
    public void Create_SessionsJson_PrintsSessionStatsInSeconds()
    {
        var result = Run(SessionsCommand.Create(DataService), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(4, document.RootElement.GetProperty("total").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("longestDurationSeconds", out _));
    }

    /// <summary>
    /// Verifies that hours --json prints the provider's hourly activity.
    /// </summary>
    [Fact]
    public void Create_HoursJson_PrintsHourlyActivity()
    {
        var result = Run(HoursCommand.Create(DataService), "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(14, Assert.Single(document.RootElement.EnumerateArray()).GetProperty("hour").GetInt32());
    }

    /// <summary>
    /// Verifies that today renders the day's table and the hourly chart, which needs the recent activity.
    /// </summary>
    [Fact]
    public void Create_TodayTable_RendersSummaryAndLoadsRecentActivity()
    {
        var result = Run(TodayCommand.Create(DataService, Clock));

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("2026-10-09 Summary", result.Out);
        Assert.Contains("Tokens by Hour", result.Out);
        Assert.Contains("09:00", result.Out);
        Assert.Equal(1, Provider.RecentActivityCalls);
        Assert.Equal(TimeSpan.FromHours(12), Provider.LastRecentActivityWindow);
    }

    /// <summary>
    /// Verifies that another day renders its table without the since-midnight chart or loading recent activity.
    /// </summary>
    [Fact]
    public void Create_TodayTableWithOtherDate_RendersSummaryWithoutRecentActivity()
    {
        var result = Run(TodayCommand.Create(DataService, Clock), "--date", "2026-10-08");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("2026-10-08 Summary", result.Out);
        Assert.Equal(0, Provider.RecentActivityCalls);
    }

    /// <summary>
    /// Verifies that the other report commands render a table when --json is not given.
    /// </summary>
    [Fact]
    public void Create_ReportCommandsTable_RenderFormattedOutput()
    {
        var week = Run(WeekCommand.Create(DataService, Clock), "--last", "2");
        var models = Run(ModelsCommand.Create(DataService));
        var hours = Run(HoursCommand.Create(DataService));
        var sessions = Run(SessionsCommand.Create(DataService));

        Assert.Contains("2026-10-08 ~ 2026-10-09", week.Out);
        Assert.Contains("Input", models.Out);
        Assert.Contains("14", hours.Out);
        Assert.Contains("Total Sessions", sessions.Out);
    }
}
