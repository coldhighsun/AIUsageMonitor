using AIUsageMonitor.Cli.Commands;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="LimitsSettingsStore"/> and the day-name parsing of <see cref="LimitsAnchors"/>.
/// </summary>
public class LimitsSettingsStoreTests : IDisposable
{
    /// <summary>
    /// The directory the settings files of a test are written to.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"aimon-{Guid.NewGuid()}");

    /// <summary>
    /// Deletes the test directory.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that saved settings are read back unchanged and that no temporary file is left behind.
    /// </summary>
    [Fact]
    public void Save_ThenLoad_RoundTripsAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_dir, "limits-settings.json");
        var settings = new LimitsSettings(new DateTimeOffset(2026, 10, 1, 18, 30, 0, TimeSpan.Zero), "Mon 09:00", 12.5m, 80m);

        var saved = LimitsSettingsStore.Save(settings, path);
        var loaded = LimitsSettingsStore.Load(path);

        Assert.True(saved);
        Assert.Equal(settings, loaded);
        Assert.Equal([path], Directory.GetFiles(_dir));
    }

    /// <summary>
    /// Verifies that a path that cannot be written reports failure instead of throwing.
    /// </summary>
    [Fact]
    public void Save_PathBelowAFile_ReturnsFalseInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "not a directory");

        var saved = LimitsSettingsStore.Save(LimitsSettings.Empty, Path.Combine(blocker, "limits-settings.json"));

        Assert.False(saved);
    }

    /// <summary>
    /// Verifies that a corrupt settings file is treated as no settings.
    /// </summary>
    [Fact]
    public void Load_CorruptFile_ReturnsEmptySettings()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "limits-settings.json");
        File.WriteAllText(path, "{ not json");

        var loaded = LimitsSettingsStore.Load(path);

        Assert.Equal(LimitsSettings.Empty, loaded);
    }

    /// <summary>
    /// Verifies that a missing settings file is treated as no settings.
    /// </summary>
    [Fact]
    public void Load_MissingFile_ReturnsEmptySettings()
    {
        var loaded = LimitsSettingsStore.Load(Path.Combine(_dir, "missing.json"));

        Assert.Equal(LimitsSettings.Empty, loaded);
    }

    /// <summary>
    /// Verifies that unambiguous day names and abbreviations are accepted.
    /// </summary>
    /// <param name="text">The weekly reset text.</param>
    /// <param name="expected">The day it should resolve to.</param>
    [Theory]
    [InlineData("Mon 09:00", DayOfWeek.Monday)]
    [InlineData("tu 09:00", DayOfWeek.Tuesday)]
    [InlineData("Thu 09:00", DayOfWeek.Thursday)]
    [InlineData("Sunday 09:00", DayOfWeek.Sunday)]
    public void TryParseWeekReset_UnambiguousDay_ResolvesIt(string text, DayOfWeek expected)
    {
        var parsed = LimitsAnchors.TryParseWeekReset(text, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.Day);
    }

    /// <summary>
    /// Verifies that an abbreviation that fits two days is rejected instead of silently picking one.
    /// </summary>
    /// <param name="text">The weekly reset text.</param>
    [Theory]
    [InlineData("S 09:00")]
    [InlineData("T 09:00")]
    public void TryParseWeekReset_AmbiguousDay_IsRejected(string text)
    {
        var parsed = LimitsAnchors.TryParseWeekReset(text, out _);

        Assert.False(parsed);
    }
}
