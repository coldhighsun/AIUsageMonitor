using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Models;

/// <summary>
/// Tests for grouping sessions into projects and for the project filter.
/// </summary>
public class ProjectUsageTests
{
    /// <summary>
    /// Creates a session with only the fields these tests care about.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="projectKey">The project folder.</param>
    /// <param name="projectPath">The working directory, if any.</param>
    /// <param name="start">The start time, as a day of August 2026.</param>
    /// <param name="tokens">The tokens, all attributed to <c>sonnet-5</c>.</param>
    /// <param name="cost">The estimated cost.</param>
    /// <returns>The session.</returns>
    private static SessionUsage Session(string id, string projectKey, string? projectPath, int start, long tokens, decimal cost)
    {
        var begin = new DateTimeOffset(2026, 8, start, 10, 0, 0, TimeSpan.Zero);
        return new SessionUsage(
            id, projectKey, projectPath, begin, begin.AddMinutes(30), 4, 2, tokens, tokens, 0, 0, 0,
            new Dictionary<string, long> { ["sonnet-5"] = tokens }, cost);
    }

    /// <summary>
    /// Verifies that sessions of one project folder are summed, ignoring the case of the folder name.
    /// </summary>
    [Fact]
    public void FromSessions_SessionsOfOneFolder_AreSummed()
    {
        var sessions = new[]
        {
            Session("s1", "E--repos-App", "E:/repos/App", 3, 100, 1m),
            Session("s2", "e--repos-app", null, 1, 50, 0.5m),
            Session("s3", "E--repos-Other", "E:/repos/Other", 2, 7, 0.1m),
        };

        var projects = ProjectUsage.FromSessions(sessions);

        Assert.Equal(2, projects.Count);
        var app = projects.Single(p => p.ProjectKey.Equals("E--repos-App", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, app.Sessions);
        Assert.Equal(8, app.Messages);
        Assert.Equal(4, app.ToolCalls);
        Assert.Equal(150, app.TotalTokens);
        Assert.Equal(150, app.TokensByModel["sonnet-5"]);
        Assert.Equal(1.5m, app.EstimatedCost);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), app.FirstActivity);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 10, 30, 0, TimeSpan.Zero), app.LastActivity);
    }

    /// <summary>
    /// Verifies that the project path comes from the earliest session that has one, and may be missing.
    /// </summary>
    [Fact]
    public void FromSessions_ProjectPath_ComesFromTheEarliestSessionThatHasOne()
    {
        var sessions = new[]
        {
            Session("late", "p", "C:/late", 5, 1, 0m),
            Session("early", "p", null, 1, 1, 0m),
            Session("middle", "p", "C:/middle", 3, 1, 0m),
            Session("alone", "q", null, 1, 1, 0m),
        };

        var projects = ProjectUsage.FromSessions(sessions);

        Assert.Equal("C:/middle", projects.Single(p => p.ProjectKey == "p").ProjectPath);
        Assert.Null(projects.Single(p => p.ProjectKey == "q").ProjectPath);
    }

    /// <summary>
    /// Verifies that no sessions give no projects.
    /// </summary>
    [Fact]
    public void FromSessions_NoSessions_ReturnsNoProjects()
    {
        Assert.Empty(ProjectUsage.FromSessions([]));
    }

    /// <summary>
    /// Verifies that the duration is the wall-clock time between the first and last message.
    /// </summary>
    [Fact]
    public void Duration_Session_IsTheSpanBetweenStartAndEnd()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Session("s", "p", null, 1, 1, 0m).Duration);
    }

    /// <summary>
    /// Verifies the project filter against paths, encoded folder names and separators.
    /// </summary>
    [Theory]
    [InlineData("repos/app", true)]
    [InlineData("REPOS\\APP", true)]
    [InlineData("c:/repos/app", true)]
    [InlineData("--repos-app", true)]
    [InlineData("nothing", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    public void Matches_PathOrFolderName_IsCaseAndSeparatorInsensitive(string? text, bool expected)
    {
        var session = Session("s", "C--repos-app", "C:/repos/app", 1, 1, 0m);

        Assert.Equal(expected, ProjectFilter.Matches(session, text));
    }

    /// <summary>
    /// Verifies that the filter still matches the folder name when no working directory was recorded.
    /// </summary>
    [Fact]
    public void Matches_NoProjectPath_MatchesTheFolderName()
    {
        var session = Session("s", "C--repos-app", null, 1, 1, 0m);

        Assert.True(ProjectFilter.Matches(session, "repos-app"));
        Assert.False(ProjectFilter.Matches(session, "repos/app"));
    }
}
