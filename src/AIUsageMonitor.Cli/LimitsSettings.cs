using AIUsageMonitor.Cli.Rendering;
using Spectre.Console;
using System.Text.Json;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Persisted user preferences for the <c>limits</c> command: the real reset times used to pin the
/// "Current session"/"This Week" windows to the account's own schedule.
/// </summary>
/// <param name="SessionResetAt">The real reset time of the current session window, if configured.</param>
/// <param name="WeekResetAt">The real weekly reset time, as originally typed (e.g. "Mon 09:00"), if configured.</param>
/// <param name="SessionCostLimit">
/// The account's session (5h) usage limit, expressed as an estimated USD cost budget rather than a
/// raw token count - Anthropic's real usage gating weights tokens by model and type (an Opus token
/// is far more "expensive" toward the limit than a Haiku one), which estimated cost already
/// approximates far better than a flat token sum. Claude itself only ever shows a usage
/// *percentage*, never the underlying limit, so this value is not typed in directly - it is derived
/// once (<c>currentCost / (enteredPercent / 100)</c>) from the percentage the user read off
/// Claude's usage display and the estimated cost this tool had computed for the window at that moment.
/// Once derived it is persisted and reused so the usage-progress bar can keep tracking live as
/// cost accrues, without asking the user again every refresh.
/// </param>
/// <param name="WeekCostLimit">The account's weekly usage limit, derived the same way as <see cref="SessionCostLimit"/>.</param>
public sealed record LimitsSettings(
    DateTimeOffset? SessionResetAt,
    string? WeekResetAt,
    decimal? SessionCostLimit = null,
    decimal? WeekCostLimit = null)
{
    /// <summary>An empty settings instance with no configured reset times or usage limits.</summary>
    public static readonly LimitsSettings Empty = new(null, null, null, null);
}

/// <summary>
/// Loads and saves <see cref="LimitsSettings"/> to a JSON file under the user's local application data folder.
/// </summary>
public static class LimitsSettingsStore
{
    /// <summary>
    /// The location of the settings file.
    /// </summary>
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "limits-settings.json");

    /// <summary>
    /// Loads the persisted <see cref="LimitsSettings"/>, or <see cref="LimitsSettings.Empty"/> if none exist
    /// or the file cannot be read.
    /// </summary>
    /// <returns>The loaded settings.</returns>
    public static LimitsSettings Load()
    {
        return Load(FilePath);
    }

    /// <summary>
    /// Saves the given <see cref="LimitsSettings"/>, creating the containing directory if needed.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    /// <returns><see langword="true"/> if the settings were written; <see langword="false"/> if the file could not be written.</returns>
    public static bool Save(LimitsSettings settings)
    {
        return Save(settings, FilePath);
    }

    /// <summary>
    /// Saves the settings and, if that fails, tells the user instead of crashing: a settings file that cannot be
    /// written (read-only profile, full disk) must not take down the command that is running.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    public static void SaveOrWarn(LimitsSettings settings)
    {
        if (!Save(settings))
        {
            AnsiConsole.MarkupLine(ConsoleMarkup.Colored("yellow", $"Could not save your settings to {FilePath}; they will not be remembered."));
        }
    }

    /// <summary>
    /// Loads settings from a specific file.
    /// </summary>
    /// <param name="path">The settings file.</param>
    /// <returns>The loaded settings, or <see cref="LimitsSettings.Empty"/> if the file is missing, unreadable or not valid settings JSON.</returns>
    internal static LimitsSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<LimitsSettings>(File.ReadAllText(path)) ?? LimitsSettings.Empty
                : LimitsSettings.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return LimitsSettings.Empty;
        }
    }

    /// <summary>
    /// Saves settings to a specific file. The content goes to a temporary file first and is then moved into place,
    /// so a crash mid-write cannot leave a truncated settings file behind.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    /// <param name="path">The settings file.</param>
    /// <returns><see langword="true"/> if the file was written.</returns>
    internal static bool Save(LimitsSettings settings, string path)
    {
        var tempPath = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings));
            File.Move(tempPath, path, overwrite: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move may be what failed, which would otherwise leave the temporary file behind.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                // Best effort: nothing more can be done about a file that cannot be removed either.
            }

            return false;
        }
    }
}
