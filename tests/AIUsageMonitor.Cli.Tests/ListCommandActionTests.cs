using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Runs <c>aimon projects</c> and <c>aimon sessions --list</c> end to end against a fake provider and checks what they print
/// and return.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ListCommandActionTests : CommandActionTestBase
{
    /// <summary>
    /// Creates a session for the fake provider to serve.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="project">The project path.</param>
    /// <param name="tokens">The total tokens.</param>
    /// <param name="cost">The estimated cost.</param>
    /// <returns>The session.</returns>
    private static SessionUsage SessionOf(string id, string? project, long tokens, decimal cost)
    {
        var start = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
        return new SessionUsage(
            id, "C--repos-app", project, start, start.AddMinutes(30), 4, 1, tokens, tokens, 0, 0, 0,
            new Dictionary<string, long> { ["sonnet-5"] = tokens }, cost);
    }

    /// <summary>
    /// Verifies that projects covers the last 30 days by default and passes the model filter to the provider.
    /// </summary>
    [Fact]
    public void Create_ProjectsJson_UsesTheDefaultRangeAndModelFilter()
    {
        Provider.Sessions = [SessionOf("s1", "C:/repos/app", 100, 1m)];

        var result = Run(ProjectsCommand.Create(DataService, Clock), "--model", "opus", "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Equal((new DateOnly(2026, 9, 10), new DateOnly(2026, 10, 9), "opus"), Provider.LastSessionUsageRequest);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal("2026-09-10", document.RootElement.GetProperty("from").GetString());
        Assert.Equal("2026-10-09", document.RootElement.GetProperty("to").GetString());
        Assert.Equal("C:/repos/app", document.RootElement.GetProperty("projects")[0].GetProperty("projectPath").GetString());
    }

    /// <summary>
    /// Verifies that projects honors --last, --sort, --top and --project, and reports the matches before truncation.
    /// </summary>
    [Fact]
    public void Create_ProjectsJsonWithListOptions_FiltersSortsAndTruncates()
    {
        Provider.Sessions =
        [
            SessionOf("s1", "C:/repos/app", 100, 1m) with { ProjectKey = "k-app" },
            SessionOf("s2", "C:/repos/lib", 900, 2m) with { ProjectKey = "k-lib" },
            SessionOf("s3", "C:/repos/tool", 500, 9m) with { ProjectKey = "k-tool" },
        ];

        var byCost = Run(ProjectsCommand.Create(DataService, Clock), "--last", "7", "--sort", "cost", "--top", "2", "--json");
        var requestedFrom = Provider.LastSessionUsageRequest!.Value.From;
        var filtered = Run(ProjectsCommand.Create(DataService, Clock), "--project", "repos/LIB", "--json");

        using var costDocument = JsonDocument.Parse(byCost.Out);
        Assert.Equal(new DateOnly(2026, 10, 3), requestedFrom);
        Assert.Equal(3, costDocument.RootElement.GetProperty("totalProjects").GetInt32());
        Assert.Equal(["k-tool", "k-lib"], costDocument.RootElement.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("projectKey").GetString()));
        using var filteredDocument = JsonDocument.Parse(filtered.Out);
        Assert.Equal("k-lib", Assert.Single(filteredDocument.RootElement.GetProperty("projects").EnumerateArray()).GetProperty("projectKey").GetString());
    }

    /// <summary>
    /// Verifies that projects prints a table, and a hint on stderr when nothing matches.
    /// </summary>
    [Fact]
    public void Create_ProjectsTable_RendersRowsOrHintsWhenEmpty()
    {
        Provider.Sessions = [SessionOf("s1", "C:/repos/[app]", 1500, 1.5m)];
        var table = Run(ProjectsCommand.Create(DataService, Clock));
        Provider.Sessions = [];
        var empty = Run(ProjectsCommand.Create(DataService, Clock));

        Assert.True(table.ExitCode == 0, table.Error);
        Assert.Contains("[app]", table.Out);
        Assert.Contains("2026-09-10 ~ 2026-10-09", table.Out);
        Assert.Equal(0, empty.ExitCode);
        Assert.Equal("", empty.Out);
        Assert.Contains("No usage found between 2026-09-10 and 2026-10-09", empty.Error);
    }

    /// <summary>
    /// Verifies that ordering by messages shows the Messages column, which is otherwise left out.
    /// </summary>
    [Fact]
    public void Create_ProjectsTable_ShowsMessagesColumnOnlyWhenSortedByMessages()
    {
        Provider.Sessions = [SessionOf("s1", "C:/repos/app", 1500, 1.5m)];

        var byTokens = Run(ProjectsCommand.Create(DataService, Clock));
        var byMessages = Run(ProjectsCommand.Create(DataService, Clock), "--sort", "messages");

        Assert.DoesNotContain("Messages", byTokens.Out);
        Assert.Contains("Messages", byMessages.Out);
    }

    /// <summary>
    /// Verifies that an invalid date range makes projects fail with a message and exit code 1.
    /// </summary>
    [Fact]
    public void Create_ProjectsInvalidRange_ReturnsOneWithMessage()
    {
        var result = Run(ProjectsCommand.Create(DataService, Clock), "--from", "2026-10-05", "--to", "2026-10-01");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("must not be after", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that sessions --list prints the sessions in the requested order with durations in seconds.
    /// </summary>
    [Fact]
    public void Create_SessionsListJson_OrdersAndReportsSeconds()
    {
        Provider.Sessions = [SessionOf("small", null, 10, 1m), SessionOf("big", null, 900, 5m), SessionOf("mid", null, 500, 3m)];

        var result = Run(SessionsCommand.Create(DataService, Clock), "--list", "--top", "2", "--json");

        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(3, document.RootElement.GetProperty("totalSessions").GetInt32());
        var sessions = document.RootElement.GetProperty("sessions").EnumerateArray().ToList();
        Assert.Equal(["big", "mid"], sessions.Select(s => s.GetProperty("sessionId").GetString()));
        Assert.Equal(1800, sessions[0].GetProperty("durationSeconds").GetDouble());
        Assert.Equal((new DateOnly(2026, 9, 10), new DateOnly(2026, 10, 9), null), Provider.LastSessionUsageRequest);
    }

    /// <summary>
    /// Verifies that sessions --list prints a table, and a hint on stderr when nothing matches.
    /// </summary>
    [Fact]
    public void Create_SessionsListTable_RendersRowsOrHintsWhenEmpty()
    {
        Provider.Sessions = [SessionOf("abcdef0123456789", "C:/repos/app", 1500, 1.5m)];
        var table = Run(SessionsCommand.Create(DataService, Clock), "--list");
        Provider.Sessions = [];
        var empty = Run(SessionsCommand.Create(DataService, Clock), "--list");

        Assert.Contains("abcdef01", table.Out);
        Assert.DoesNotContain("0123456789", table.Out);
        Assert.Equal(0, empty.ExitCode);
        Assert.Contains("No sessions found between 2026-09-10 and 2026-10-09", empty.Error);
    }

    /// <summary>
    /// Verifies that the list options are rejected without --list instead of being ignored.
    /// </summary>
    [Theory]
    [InlineData("--top 3")]
    [InlineData("--sort cost")]
    [InlineData("--project app")]
    [InlineData("--model opus")]
    [InlineData("--last 7")]
    [InlineData("--month 2026-09")]
    public void Create_SessionsWithoutListFlag_RejectsListOptions(string options)
    {
        var result = Run(SessionsCommand.Create(DataService, Clock), options.Split(' '));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("require --list", result.Error);
        Assert.Equal("", result.Out);
    }

    /// <summary>
    /// Verifies that sessions without --list still prints the overall statistics, not a list.
    /// </summary>
    [Fact]
    public void Create_SessionsWithoutListFlag_StillPrintsOverallStatistics()
    {
        var result = Run(SessionsCommand.Create(DataService, Clock), "--json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Out);
        Assert.Equal(4, document.RootElement.GetProperty("total").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("sessions", out _));
    }
}
