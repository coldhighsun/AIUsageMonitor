using AIUsageMonitor.Core.Providers.Claude;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class SessionActivityTrackerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    [Fact]
    public void LatestWriteUtc_NoSessionFiles_ReturnsMinValue()
    {
        var sut = new SessionActivityTracker(new ClaudeDataLocator(_tempDir));

        Assert.Equal(DateTime.MinValue, sut.LatestWriteUtc);
    }

    [Fact]
    public void LatestWriteUtc_ScansDiskOnFirstAccess()
    {
        var project = Path.Combine(_tempDir, "projects", "proj1");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "a.jsonl");
        File.WriteAllText(file, "");
        var writeTime = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, writeTime);

        var sut = new SessionActivityTracker(new ClaudeDataLocator(_tempDir));

        Assert.Equal(writeTime, sut.LatestWriteUtc);
    }

    [Fact]
    public void Observe_NewerThanBaseline_AdvancesLatestWriteUtc()
    {
        var sut = new SessionActivityTracker(new ClaudeDataLocator(_tempDir));
        var older = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

        sut.Observe(older);
        sut.Observe(newer);

        Assert.Equal(newer, sut.LatestWriteUtc);
    }

    [Fact]
    public void Observe_OlderThanCurrent_DoesNotRegress()
    {
        var sut = new SessionActivityTracker(new ClaudeDataLocator(_tempDir));
        var newer = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

        sut.Observe(newer);
        sut.Observe(older);

        Assert.Equal(newer, sut.LatestWriteUtc);
    }

    [Fact]
    public void Observe_BeforeFirstAccess_SkipsDiskScan()
    {
        var project = Path.Combine(_tempDir, "projects", "proj1");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "a.jsonl");
        File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var sut = new SessionActivityTracker(new ClaudeDataLocator(_tempDir));
        var observed = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        sut.Observe(observed);

        Assert.Equal(observed, sut.LatestWriteUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
