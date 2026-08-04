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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
