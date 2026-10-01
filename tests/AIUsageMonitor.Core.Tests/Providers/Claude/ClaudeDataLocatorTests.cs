using AIUsageMonitor.Core.Providers.Claude;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Providers.Claude;

public class ClaudeDataLocatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    [Fact]
    public void Paths_AreComposedFromClaudeDir()
    {
        var sut = new ClaudeDataLocator(_tempDir);

        Assert.Equal(_tempDir, sut.ClaudeDir);
        Assert.Equal(Path.Combine(_tempDir, "stats-cache.json"), sut.StatsCachePath);
        Assert.Equal(Path.Combine(_tempDir, "history.jsonl"), sut.HistoryPath);
        Assert.Equal(Path.Combine(_tempDir, "projects"), sut.ProjectsDir);
    }

    [Fact]
    public void GetSessionFiles_NoProjectsDir_ReturnsEmpty()
    {
        var sut = new ClaudeDataLocator(_tempDir);

        Assert.Empty(sut.GetSessionFiles());
    }

    [Fact]
    public void GetSessionFiles_EnumeratesJsonlFilesAcrossProjectDirs()
    {
        var project1 = Path.Combine(_tempDir, "projects", "proj1");
        var project2 = Path.Combine(_tempDir, "projects", "proj2");
        Directory.CreateDirectory(project1);
        Directory.CreateDirectory(project2);
        File.WriteAllText(Path.Combine(project1, "a.jsonl"), "");
        File.WriteAllText(Path.Combine(project1, "notes.txt"), "");
        File.WriteAllText(Path.Combine(project2, "b.jsonl"), "");

        var sut = new ClaudeDataLocator(_tempDir);
        var files = sut.GetSessionFiles();

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.EndsWith("a.jsonl"));
        Assert.Contains(files, f => f.EndsWith("b.jsonl"));
    }

    [Fact]
    public void GetProjectDirectories_NoProjectsDir_ReturnsEmpty()
    {
        var sut = new ClaudeDataLocator(_tempDir);

        Assert.Empty(sut.GetProjectDirectories());
    }

    [Fact]
    public void GetProjectDirectories_ReturnsEncodedNameAndFullPath()
    {
        var project1 = Path.Combine(_tempDir, "projects", "proj1");
        Directory.CreateDirectory(project1);

        var sut = new ClaudeDataLocator(_tempDir);
        var dirs = sut.GetProjectDirectories();

        Assert.Single(dirs);
        Assert.Equal("proj1", dirs[0].EncodedName);
        Assert.Equal(project1, dirs[0].FullPath);
    }

    /// <summary>
    /// A clock whose time is set explicitly.
    /// </summary>
    /// <param name="now">The initial time.</param>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        /// <summary>
        /// Gets or sets the current time.
        /// </summary>
        public DateTimeOffset Now { get; set; } = now;

        /// <summary>
        /// Returns the configured time.
        /// </summary>
        /// <returns>The configured time.</returns>
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Creates a project directory containing the given (empty) session files.
    /// </summary>
    /// <param name="names">The file names to create.</param>
    /// <returns>The full paths of the created files.</returns>
    private string[] CreateSessions(params string[] names)
    {
        var dir = Path.Combine(_tempDir, "projects", "proj");
        Directory.CreateDirectory(dir);

        return [.. names.Select(n =>
        {
            var path = Path.Combine(dir, n);
            File.WriteAllText(path, "");

            return path;
        })];
    }

    /// <summary>
    /// Verifies that without change tracking every call sees the current disk state.
    /// </summary>
    [Fact]
    public void GetSessionFiles_TrackingDisabled_RescansEveryCall()
    {
        var sut = new ClaudeDataLocator(_tempDir);
        CreateSessions("a.jsonl");
        Assert.Single(sut.GetSessionFiles());

        CreateSessions("b.jsonl");

        Assert.Equal(2, sut.GetSessionFiles().Count);
    }

    /// <summary>
    /// Verifies that with tracking the listing is cached: the same snapshot is returned and unreported disk changes are not seen.
    /// </summary>
    [Fact]
    public void GetSessionFiles_TrackingEnabled_ReturnsCachedSnapshot()
    {
        var sut = new ClaudeDataLocator(_tempDir);
        sut.EnableChangeTracking();
        CreateSessions("a.jsonl");
        var first = sut.GetSessionFiles();

        CreateSessions("b.jsonl");
        var second = sut.GetSessionFiles();

        Assert.Same(first, second);
        Assert.Single(second);
    }

    /// <summary>
    /// Verifies that created, deleted and renamed files reported to the locator are reflected without rescanning, and that earlier snapshots are not mutated.
    /// </summary>
    [Fact]
    public void NotifyFileCreatedAndDeleted_UpdateCachedListing()
    {
        var sut = new ClaudeDataLocator(_tempDir);
        sut.EnableChangeTracking();
        var files = CreateSessions("a.jsonl");
        var before = sut.GetSessionFiles();
        var added = Path.Combine(_tempDir, "projects", "proj", "new.jsonl");

        sut.NotifyFileCreated(added);
        sut.NotifyFileCreated(added);
        var afterCreate = sut.GetSessionFiles();
        sut.NotifyFileDeleted(files[0]);
        var afterDelete = sut.GetSessionFiles();

        Assert.Single(before);
        Assert.Equal(2, afterCreate.Count);
        Assert.Equal([added], afterDelete);
    }

    /// <summary>
    /// Verifies that an event arriving before any listing is cached is harmless and the first scan sees the disk.
    /// </summary>
    [Fact]
    public void NotifyFileCreated_BeforeFirstListing_IsIgnoredAndScanSeesDisk()
    {
        var sut = new ClaudeDataLocator(_tempDir);
        sut.EnableChangeTracking();
        var files = CreateSessions("a.jsonl");

        sut.NotifyFileCreated(Path.Combine(_tempDir, "projects", "proj", "ghost.jsonl"));

        Assert.Equal(files, sut.GetSessionFiles());
    }

    /// <summary>
    /// Verifies that invalidating the listing (lost notifications) forces a rescan.
    /// </summary>
    [Fact]
    public void InvalidateSessionFiles_ForcesRescan()
    {
        var sut = new ClaudeDataLocator(_tempDir);
        sut.EnableChangeTracking();
        CreateSessions("a.jsonl");
        sut.GetSessionFiles();
        CreateSessions("b.jsonl");

        sut.InvalidateSessionFiles();

        Assert.Equal(2, sut.GetSessionFiles().Count);
    }

    /// <summary>
    /// Verifies that a cached listing is rescanned once it is older than the safety-net age, even with no events.
    /// </summary>
    [Fact]
    public void GetSessionFiles_ListingOlderThanMaxAge_Rescans()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        var sut = new ClaudeDataLocator(_tempDir, clock);
        sut.EnableChangeTracking();
        CreateSessions("a.jsonl");
        sut.GetSessionFiles();
        CreateSessions("b.jsonl");

        clock.Now += ClaudeDataLocator.ListingMaxAge - TimeSpan.FromSeconds(1);
        Assert.Single(sut.GetSessionFiles());

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.Equal(2, sut.GetSessionFiles().Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
