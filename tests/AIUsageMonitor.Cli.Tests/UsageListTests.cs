using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for the sorting, option parsing, rendering and JSON shapes of the project and session lists.
/// </summary>
public class UsageListTests
{
    /// <summary>
    /// Creates a session with only the fields these tests care about.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="startHour">The start time, as an hour of 2026-08-02 (UTC).</param>
    /// <param name="tokens">The total tokens.</param>
    /// <param name="cost">The estimated cost.</param>
    /// <param name="messages">The message count.</param>
    /// <param name="minutes">The duration in minutes.</param>
    /// <param name="path">The project path.</param>
    /// <returns>The session.</returns>
    private static SessionUsage Session(
        string id, int startHour, long tokens, decimal cost, int messages = 1, int minutes = 10, string? path = "C:/repos/app")
    {
        var start = new DateTimeOffset(2026, 8, 2, startHour, 0, 0, TimeSpan.Zero);
        return new SessionUsage(
            id, "C--repos-app", path, start, start.AddMinutes(minutes), messages, 2, tokens, tokens, 0, 0, 0,
            new Dictionary<string, long> { ["sonnet-5"] = tokens }, cost);
    }

    /// <summary>
    /// Creates a project with only the fields these tests care about.
    /// </summary>
    /// <param name="key">The project folder.</param>
    /// <param name="sessions">The session count.</param>
    /// <param name="messages">The message count.</param>
    /// <param name="tokens">The total tokens.</param>
    /// <param name="cost">The estimated cost.</param>
    /// <returns>The project.</returns>
    private static ProjectUsage Project(string key, int sessions, int messages, long tokens, decimal cost)
    {
        var at = new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero);
        return new ProjectUsage(key, null, sessions, messages, 0, tokens, [], cost, at, at);
    }

    /// <summary>
    /// Verifies every project sort key, largest first.
    /// </summary>
    [Theory]
    [InlineData("tokens", "b,c,a")]
    [InlineData("cost", "c,a,b")]
    [InlineData("sessions", "a,c,b")]
    [InlineData("messages", "c,b,a")]
    public void SortProjects_EachKey_OrdersLargestFirst(string sort, string expected)
    {
        var projects = new[] { Project("a", 9, 1, 10, 3m), Project("b", 1, 5, 30, 1m), Project("c", 5, 9, 20, 7m) };

        var sorted = UsageSorting.SortProjects(projects, sort, null);

        Assert.Equal(expected, string.Join(",", sorted.Select(p => p.ProjectKey)));
    }

    /// <summary>
    /// Verifies every session sort key, largest (or newest) first.
    /// </summary>
    [Theory]
    [InlineData("tokens", "b,c,a")]
    [InlineData("cost", "c,a,b")]
    [InlineData("messages", "a,c,b")]
    [InlineData("duration", "c,a,b")]
    [InlineData("start", "c,b,a")]
    public void SortSessions_EachKey_OrdersLargestFirst(string sort, string expected)
    {
        var sessions = new[]
        {
            Session("a", 8, 10, 3m, messages: 9, minutes: 20),
            Session("b", 9, 30, 1m, messages: 1, minutes: 5),
            Session("c", 10, 20, 7m, messages: 5, minutes: 60),
        };

        var sorted = UsageSorting.SortSessions(sessions, sort, null);

        Assert.Equal(expected, string.Join(",", sorted.Select(s => s.SessionId)));
    }

    /// <summary>
    /// Verifies that equal keys keep a stable order, by identifier.
    /// </summary>
    [Fact]
    public void SortSessions_EqualKeys_BreaksTiesBySessionId()
    {
        var sessions = new[] { Session("z", 8, 10, 1m), Session("a", 9, 10, 1m), Session("m", 10, 10, 1m) };

        var sorted = UsageSorting.SortSessions(sessions, "tokens", null);

        Assert.Equal("a,m,z", string.Join(",", sorted.Select(s => s.SessionId)));
    }

    /// <summary>
    /// Verifies that --top keeps the first rows only after ordering, and that a larger value keeps everything.
    /// </summary>
    [Fact]
    public void SortProjects_Top_KeepsTheLargestRows()
    {
        var projects = new[] { Project("a", 1, 1, 10, 1m), Project("b", 1, 1, 30, 1m), Project("c", 1, 1, 20, 1m) };

        Assert.Equal("b,c", string.Join(",", UsageSorting.SortProjects(projects, "tokens", 2).Select(p => p.ProjectKey)));
        Assert.Equal(3, UsageSorting.SortProjects(projects, "tokens", 10).Count);
    }

    /// <summary>
    /// Verifies that the start of a session shows its year only when the range spans more than one year.
    /// </summary>
    [Fact]
    public void BuildSessionList_RangeAcrossYears_ShowsTheYearOfTheStart()
    {
        var session = Session("s1", 9, 1500, 1.5m);
        var layout = new TableLayout(200, false);

        var sameYear = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionList([session], 1, new(2026, 1, 1), new(2026, 12, 31), layout));
        var acrossYears = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionList([session], 1, new(2025, 6, 1), new(2026, 12, 31), layout));

        Assert.DoesNotContain("2026-08-02 ", sameYear.Split('\n').Last(line => line.Contains("s1")));
        Assert.Contains("2026-08-02 ", acrossYears.Split('\n').Last(line => line.Contains("s1")));
    }

    /// <summary>
    /// Verifies that only ordering by messages asks the tables for a Messages column.
    /// </summary>
    [Theory]
    [InlineData("messages", true)]
    [InlineData("tokens", false)]
    [InlineData("cost", false)]
    [InlineData("sessions", false)]
    [InlineData("duration", false)]
    [InlineData("start", false)]
    public void ShowsMessages_SortKey_IsTrueOnlyForMessages(string sort, bool expected)
    {
        Assert.Equal(expected, UsageSorting.ShowsMessages(sort));
        Assert.Contains(UsageSorting.MessagesKey, UsageSorting.ProjectKeys);
        Assert.Contains(UsageSorting.MessagesKey, UsageSorting.SessionKeys);
    }

    /// <summary>
    /// Verifies that the list commands accept their options and reject bad values.
    /// </summary>
    [Fact]
    public void Create_ListCommands_ValidateTopAndSort()
    {
        var projects = ProjectsCommand.Create(null!);
        var sessions = SessionsCommand.Create(null!);

        Assert.Empty(projects.Parse("--top 5 --sort cost --project app --model opus --last 7 --json").Errors);
        Assert.Empty(projects.Parse("--sort COST").Errors);
        Assert.NotEmpty(projects.Parse("--top 0").Errors);
        Assert.NotEmpty(projects.Parse("--sort bogus").Errors);
        Assert.NotEmpty(projects.Parse("--sort start").Errors);
        Assert.Empty(sessions.Parse("--list --sort start --top 3 --month 2026-08").Errors);
        Assert.NotEmpty(sessions.Parse("--list --sort sessions").Errors);
    }

    /// <summary>
    /// Verifies that only options typed on the command line count as supplied, not defaults.
    /// </summary>
    [Fact]
    public void AnySupplied_DefaultsOnly_IsFalse()
    {
        var command = new System.CommandLine.Command("x");
        var options = new ListOptions(command, UsageSorting.SessionKeys, "tokens");

        Assert.False(options.AnySupplied(command.Parse([])));
        Assert.True(options.AnySupplied(command.Parse("--sort cost")));
        Assert.True(options.AnySupplied(command.Parse("--project app")));
        Assert.Equal(new ListRequest(null, "tokens", null, null), options.Read(command.Parse([])));
        Assert.Equal(new ListRequest(3, "cost", "app", "opus"), options.Read(command.Parse("--top 3 --sort COST --project  app  --model opus")));
    }

    /// <summary>
    /// Verifies that long paths lose their beginning, keeping the part that tells projects apart.
    /// </summary>
    [Theory]
    [InlineData("C:/short", 20, "C:/short")]
    [InlineData("C:/repos/a-very-long-project-name", 12, "\u2026roject-name")]
    public void ShortenPath_Path_KeepsTheTail(string path, int max, string expected)
    {
        var shortened = SpectreRenderer.ShortenPath(path, max);

        Assert.Equal(expected, shortened);
        Assert.True(shortened.Length <= max);
    }

    /// <summary>
    /// Verifies that markup characters in paths are shown literally and do not break rendering.
    /// </summary>
    [Fact]
    public void BuildProjectUsage_PathWithMarkupCharacters_RendersThemLiterally()
    {
        var project = Project("key", 2, 3, 1500, 1.5m) with { ProjectPath = "C:/repos/[wip]/app" };

        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildProjectUsage([project], 1, new(2026, 8, 1), new(2026, 8, 31), new TableLayout(200, false)));

        Assert.Contains("[wip]", rendered);
        Assert.Contains("2026-08-01 ~ 2026-08-31", rendered);
        Assert.DoesNotContain("Showing", rendered);
    }

    /// <summary>
    /// Verifies that a truncated list says how many rows exist.
    /// </summary>
    [Fact]
    public void BuildSessionList_Truncated_ShowsHowManyExist()
    {
        var session = Session("[abc]-0123456789", 9, 1500, 1.5m, path: null);

        var rendered = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionList([session], 12, new(2026, 8, 1), new(2026, 8, 31), new TableLayout(200, false)));

        Assert.Contains("Showing 1 of 12 sessions", rendered);
        Assert.Contains("[abc]-01", rendered);
        Assert.DoesNotContain("0123456789", rendered);
    }

    /// <summary>
    /// Verifies that a narrow console shortens the project name from the left to fit, while a wide one shows it whole.
    /// </summary>
    [Fact]
    public void BuildProjectUsage_NarrowConsole_ShortensTheNameFromTheLeftToFit()
    {
        var project = Project("key", 2, 3, 1500, 1.5m) with { ProjectPath = "C:/repos/a-fairly-long-folder/the-actual-project-name" };
        var from = new DateOnly(2026, 8, 1);
        var to = new DateOnly(2026, 8, 31);

        var narrow = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildProjectUsage([project], 1, from, to, new TableLayout(70, false)), 70);
        var wide = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildProjectUsage([project], 1, from, to, new TableLayout(200, false)));

        Assert.Contains("\u2026", narrow);
        Assert.Contains("project-name", narrow);
        Assert.DoesNotContain("C:/repos", narrow);
        Assert.All(narrow.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= 70, line));
        Assert.Contains("C:/repos/a-fairly-long-folder/the-actual-project-name", wide);
    }

    /// <summary>
    /// Verifies that the Messages column appears only when asked for, in both tables.
    /// </summary>
    [Fact]
    public void BuildTables_ShowMessages_AddsTheMessagesColumnOnlyWhenAsked()
    {
        var from = new DateOnly(2026, 8, 1);
        var to = new DateOnly(2026, 8, 31);
        var project = Project("key", 2, 3, 1500, 1.5m);
        var session = Session("s1", 9, 1500, 1.5m, messages: 4321);

        var projectWithout = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildProjectUsage([project], 1, from, to, new TableLayout(200, false)));
        var projectWith = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildProjectUsage([project], 1, from, to, new TableLayout(200, true)));
        var sessionWithout = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionList([session], 1, from, to, new TableLayout(200, false)));
        var sessionWith = ConsoleMarkupTests.RenderPlain(SpectreRenderer.BuildSessionList([session], 1, from, to, new TableLayout(200, true)));

        Assert.DoesNotContain("Messages", projectWithout);
        Assert.Contains("Messages", projectWith);
        Assert.DoesNotContain("Messages", sessionWithout);
        Assert.Contains("Messages", sessionWith);
        Assert.Contains("4,321", sessionWith);
    }

    /// <summary>
    /// Verifies the JSON shape of the project and session reports, including the duration in seconds.
    /// </summary>
    [Fact]
    public void Create_Reports_SerializeWithCamelCaseAndSeconds()
    {
        var projects = ProjectListReport.Create(new(2026, 8, 1), new(2026, 8, 31), 4, [Project("key", 2, 3, 1500, 1.5m)]);
        var sessions = SessionListReport.Create(new(2026, 8, 1), new(2026, 8, 31), 7, [Session("s1", 9, 1500, 1.5m, minutes: 90)]);

        using var projectDocument = JsonDocument.Parse(JsonOutput.Serialize(projects, CliJsonContext.Default.ProjectListReport));
        using var sessionDocument = JsonDocument.Parse(JsonOutput.Serialize(sessions, CliJsonContext.Default.SessionListReport));

        Assert.Equal("2026-08-01", projectDocument.RootElement.GetProperty("from").GetString());
        Assert.Equal(4, projectDocument.RootElement.GetProperty("totalProjects").GetInt32());
        Assert.Equal("key", projectDocument.RootElement.GetProperty("projects")[0].GetProperty("projectKey").GetString());
        Assert.Equal(7, sessionDocument.RootElement.GetProperty("totalSessions").GetInt32());
        var item = sessionDocument.RootElement.GetProperty("sessions")[0];
        Assert.Equal(5400, item.GetProperty("durationSeconds").GetDouble());
        Assert.Equal(1500, item.GetProperty("tokensByModel").GetProperty("sonnet-5").GetInt64());
    }
}
