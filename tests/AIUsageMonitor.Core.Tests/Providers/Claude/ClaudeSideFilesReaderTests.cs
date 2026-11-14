using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

/// <summary>
/// Tests for <see cref="ClaudeSideFilesReader"/> and for serializing the insights through the source-generated context.
/// </summary>
public sealed class ClaudeSideFilesReaderTests : IDisposable
{
    /// <summary>
    /// The directory standing in for <c>~/.claude</c>.
    /// </summary>
    private readonly string _claudeDir = Path.Combine(Path.GetTempPath(), $"claude-side-{Guid.NewGuid()}");

    /// <summary>
    /// The reader under test.
    /// </summary>
    private readonly ClaudeSideFilesReader _reader;

    /// <summary>
    /// Creates the directory and the reader.
    /// </summary>
    public ClaudeSideFilesReaderTests()
    {
        Directory.CreateDirectory(_claudeDir);
        _reader = new(new ClaudeDataLocator(_claudeDir), NullLogger<ClaudeSideFilesReader>.Instance);
    }

    /// <summary>
    /// Deletes the directory.
    /// </summary>
    public void Dispose()
    {
        Directory.Delete(_claudeDir, recursive: true);
    }

    /// <summary>
    /// Writes a file below the Claude folder.
    /// </summary>
    /// <param name="relativePath">The path below the folder.</param>
    /// <param name="content">The file content.</param>
    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_claudeDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Verifies that a running process file is read and recognised as running, and a dead one is not.
    /// </summary>
    [Fact]
    public void GetActiveSessions_ReadsFilesAndChecksTheProcess()
    {
        Write("sessions/1.json", $$"""{"pid":{{Environment.ProcessId}},"sessionId":"s1","cwd":"/p","name":"n","status":"busy","kind":"interactive","entrypoint":"cli","version":"2.0","startedAt":1790000000000,"updatedAt":1790000005000}""");
        Write("sessions/2.json", """{"pid":2147483000,"sessionId":"s2","updatedAt":"2026-08-01T00:00:00Z"}""");
        Write("sessions/3.json", "not json");
        Write("sessions/4.json", """{"sessionId":"no-pid"}""");

        var sessions = _reader.GetActiveSessions();

        Assert.Equal(2, sessions.Count);
        var running = sessions.Single(s => s.SessionId == "s1");
        Assert.True(running.IsRunning);
        Assert.Equal(("busy", "interactive", "cli", "2.0", "n"), (running.Status, running.Kind, running.Entrypoint, running.Version, running.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1790000005000), running.UpdatedAt);
        var dead = sessions.Single(s => s.SessionId == "s2");
        Assert.False(dead.IsRunning);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), dead.UpdatedAt);
    }

    /// <summary>
    /// Verifies that a process id reused by an unrelated process, recognised by its start time, is not reported as running.
    /// </summary>
    [Fact]
    public void IsRunning_ProcessStartedAtADifferentTimeThanRecorded_IsNotRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var self = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToFileTimeUtc();

        Assert.True(ClaudeSideFilesReader.IsRunning(Environment.ProcessId, self.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(ClaudeSideFilesReader.IsRunning(Environment.ProcessId, (self - TimeSpan.FromHours(1).Ticks).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.True(ClaudeSideFilesReader.IsRunning(Environment.ProcessId, "not a number"));
    }

    /// <summary>
    /// Verifies that the two files of a sub-agent are combined, and that its task description is not read.
    /// </summary>
    [Fact]
    public void GetSubagents_CombinesMetaAndPrefixFiles()
    {
        Write("projects/p/sess1/subagents/agent-abc.meta.json", """{"agentType":"Explore","description":"SECRET TASK","spawnDepth":2}""");
        Write("projects/p/sess1/subagents/agent-abc.prefix.json", """{"v":1,"state":{"model":"claude-sonnet-5-5","fastMode":false,"is1hCacheTTL":true,"effortValue":"low","thinkingMode":"on","callCount":11}}""");
        Write("projects/p/sess1/subagents/agent-def.meta.json", """{"agentType":"Plan"}""");
        Write("projects/p/sess1/other/agent-zzz.meta.json", """{"agentType":"Ignored"}""");

        var agents = _reader.GetSubagents();

        Assert.Equal(["abc", "def"], agents.Select(a => a.AgentId));
        var abc = agents[0];
        Assert.Equal(("sess1", "Explore", 2, "claude-sonnet-5-5", false, true, "low", "on", 11),
            (abc.SessionId, abc.AgentType, abc.SpawnDepth, abc.Model, abc.FastMode, abc.OneHourCache, abc.Effort, abc.ThinkingMode, abc.CallCount));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(agents, CoreJsonContext.Default.IReadOnlyListSubagentInfo), StringComparison.Ordinal);
        Assert.Null(agents[1].Model);
    }

    /// <summary>
    /// Verifies the metadata-only inventory.
    /// </summary>
    [Fact]
    public void GetInventory_CountsFilesWithoutReadingThem()
    {
        Write("projects/p/a.jsonl", "{}\n");
        Write("projects/p/sess/subagents/agent-1.jsonl", "{}\n");
        Write("plans/x.md", "# plan");
        Write("file-history/s/1", "abc");
        Write("sessions/1.json", "{}");
        Write("history.jsonl", "");

        var inventory = _reader.GetInventory();

        Assert.Equal((1, 2, 1, 6), (inventory.ProjectFolders, inventory.TranscriptFiles, inventory.SubagentTranscriptFiles, inventory.TranscriptBytes));
        Assert.Equal((1, 1, 3, 1), (inventory.PlanFiles, inventory.FileHistoryFiles, inventory.FileHistoryBytes, inventory.ActiveSessionFiles));
        Assert.True(inventory.HasHistory);
        Assert.False(inventory.HasStatsCache);
        Assert.NotNull(inventory.NewestTranscriptWrite);
    }

    /// <summary>
    /// Verifies that a missing Claude folder is simply empty.
    /// </summary>
    [Fact]
    public void Readers_MissingFolder_ReturnEmptyResults()
    {
        var reader = new ClaudeSideFilesReader(new ClaudeDataLocator(Path.Combine(_claudeDir, "nope")), NullLogger<ClaudeSideFilesReader>.Instance);

        Assert.Empty(reader.GetActiveSessions());
        Assert.Empty(reader.GetSubagents());
        Assert.Equal(0, reader.GetInventory().TranscriptFiles);
    }

    /// <summary>
    /// Verifies that the insights serialize through the source-generated context, so any front end can use them as JSON.
    /// </summary>
    [Fact]
    public void InsightsReport_SerializesThroughTheSourceGeneratedContext()
    {
        var row = new AttributionRow("main", 1, 10, 4, 3, 2, 1, 1, 0.5m);
        var report = new InsightsReport(
            new(2026, 8, 1), new(2026, 8, 2), row,
            new Dictionary<string, IReadOnlyList<AttributionRow>> { ["kind"] = [row] },
            [new("Bash", 2, null)],
            new(2, 2, 0, new Dictionary<string, long>(), new Dictionary<string, long>()),
            new(1, 4, 2, 0, 1, 0.3, 3, 1, 0.33, 1, 0, 0, 0, [new("tools_changed", 1, 9)]),
            [], [], [], []);

        var json = JsonSerializer.Serialize(report, CoreJsonContext.Default.InsightsReport);
        var back = JsonSerializer.Deserialize(json, CoreJsonContext.Default.InsightsReport);

        Assert.NotNull(back);
        Assert.Equal(0.5m, back.Attribution["kind"][0].EstimatedCost);
        Assert.Equal("tools_changed", back.Cache.MissReasons[0].Reason);
    }
}
