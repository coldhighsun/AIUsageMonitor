using AIUsageMonitor.Core.Analytics;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIUsageMonitor.Core.Tests.Services;

/// <summary>
/// End-to-end tests of <see cref="DataService"/>'s session file watcher feeding the cached file listing,
/// using a real <see cref="FileSystemWatcher"/> on a temporary directory.
/// </summary>
public sealed class DataServiceWatcherTests : IDisposable
{
    /// <summary>
    /// A generous upper bound for waiting on a file-system notification.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The temporary Claude data directory.
    /// </summary>
    private readonly string _claudeDir = Path.Combine(Path.GetTempPath(), $"claude-{Guid.NewGuid()}");

    /// <summary>
    /// A project directory under the data directory.
    /// </summary>
    private readonly string _projectDir;

    /// <summary>
    /// The locator whose cached listing is observed.
    /// </summary>
    private readonly ClaudeDataLocator _locator;

    /// <summary>
    /// The service under test, which owns the watcher.
    /// </summary>
    private readonly DataService _sut;

    /// <summary>
    /// Creates a data directory with a project folder and a service watching it.
    /// </summary>
    public DataServiceWatcherTests()
    {
        _projectDir = Path.Combine(_claudeDir, "projects", "proj");
        Directory.CreateDirectory(_projectDir);

        _locator = new ClaudeDataLocator(_claudeDir);
        var sessionFileCache = new SessionFileCache(
            new SessionParser(NullLogger<SessionParser>.Instance), NullLogger<SessionFileCache>.Instance);
        var costCalculator = new CostCalculator();
        var tracker = new SessionActivityTracker(_locator);
        var provider = new ClaudeUsageProvider(
            _locator,
            new StatsCacheParser(),
            new StatsCacheBuilder(sessionFileCache),
            new RecentActivityBuilder(sessionFileCache, costCalculator),
            new HourlyActivityBuilder(sessionFileCache),
            new SessionBlockBuilder(sessionFileCache, costCalculator),
            tracker,
            NullLogger<ClaudeUsageProvider>.Instance);

        _sut = new(provider, new UsageAnalyzer(costCalculator), sessionFileCache, tracker, NullLogger<DataService>.Instance);
    }

    /// <summary>
    /// Stops the watcher and removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        _sut.Dispose();
        Directory.Delete(_claudeDir, recursive: true);
    }

    /// <summary>
    /// Polls until the cached listing satisfies a condition.
    /// </summary>
    /// <param name="condition">The condition on the current listing.</param>
    /// <returns><see langword="true"/> if the condition became true within the allowed time.</returns>
    private async Task<bool> EventuallyAsync(Func<IReadOnlyList<string>, bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition(_locator.GetSessionFiles()))
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Verifies that a transcript created after the listing was cached appears in it, without a rescan being requested.
    /// </summary>
    [Fact]
    public async Task CreatedFile_AppearsInCachedListing()
    {
        Assert.Empty(_locator.GetSessionFiles());
        var path = Path.Combine(_projectDir, "new.jsonl");

        File.WriteAllText(path, "{}\n");

        Assert.True(await EventuallyAsync(files => files.Contains(path, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Verifies that a transcript deleted after the listing was cached disappears from it.
    /// </summary>
    [Fact]
    public async Task DeletedFile_DisappearsFromCachedListing()
    {
        var path = Path.Combine(_projectDir, "old.jsonl");
        File.WriteAllText(path, "{}\n");
        Assert.True(await EventuallyAsync(files => files.Count == 1));

        File.Delete(path);

        Assert.True(await EventuallyAsync(files => files.Count == 0));
    }

    /// <summary>
    /// Verifies that a renamed transcript is replaced by its new name in the listing.
    /// </summary>
    [Fact]
    public async Task RenamedFile_ReplacesOldNameInCachedListing()
    {
        var oldPath = Path.Combine(_projectDir, "before.jsonl");
        var newPath = Path.Combine(_projectDir, "after.jsonl");
        File.WriteAllText(oldPath, "{}\n");
        Assert.True(await EventuallyAsync(files => files.Count == 1));

        File.Move(oldPath, newPath);

        Assert.True(await EventuallyAsync(files =>
            files.Count == 1 && string.Equals(files[0], newPath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Verifies that renaming a project directory, which raises no per-file event, still refreshes the cached listing.
    /// </summary>
    [Fact]
    public async Task RenamedProjectDirectory_UpdatesCachedListing()
    {
        File.WriteAllText(Path.Combine(_projectDir, "s.jsonl"), "{}\n");
        Assert.True(await EventuallyAsync(files => files.Count == 1));
        var movedDir = Path.Combine(_claudeDir, "projects", "renamed");
        var movedPath = Path.Combine(movedDir, "s.jsonl");

        Directory.Move(_projectDir, movedDir);

        Assert.True(await EventuallyAsync(files =>
            files.Count == 1 && string.Equals(files[0], movedPath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Verifies that a transcript created inside a project directory that did not exist when watching began is picked up.
    /// </summary>
    [Fact]
    public async Task FileInNewProjectDirectory_AppearsInCachedListing()
    {
        Assert.Empty(_locator.GetSessionFiles());
        var newDir = Path.Combine(_claudeDir, "projects", "brand-new");
        Directory.CreateDirectory(newDir);
        var path = Path.Combine(newDir, "s.jsonl");

        File.WriteAllText(path, "{}\n");

        Assert.True(await EventuallyAsync(files => files.Contains(path, StringComparer.OrdinalIgnoreCase)));
    }
}
