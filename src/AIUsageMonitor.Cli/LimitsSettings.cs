using AIUsageMonitor.Cli.Rendering;
using Spectre.Console;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Persisted user preferences for the <c>limits</c> command: the real reset times used to pin the
/// "Current session"/"This Week" windows to the account's own schedule.
/// </summary>
/// <param name="SessionResetAt">The real reset time of the current session window, if configured.</param>
/// <param name="WeekResetAt">The real weekly reset time, as originally typed (e.g. "Mon 09:00"), if configured.</param>
/// <param name="SessionCalibration">
/// The reading the account's session (5h) usage limit is derived from: the usage *percentage* the user read off
/// Claude's usage display, together with the cost this tool had counted for the window at that moment (see
/// <see cref="UsageCalibration"/>). Claude itself never shows the underlying limit, so it cannot be typed in
/// directly. The limit is derived from the reading on demand as a quota-weighted USD cost budget (see
/// <see cref="Commands.QuotaCost"/>) rather than stored, so the usage-progress bar keeps tracking live as cost accrues
/// without asking the user again, and the <see cref="CacheReadWeight"/> can be tuned without reading the percentage again.
/// </param>
/// <param name="WeekCalibration">The reading the account's weekly usage limit is derived from, kept the same way as <see cref="SessionCalibration"/>.</param>
/// <param name="CacheReadWeight">
/// The share of the list price of cache-read tokens that counts toward the usage quota (see
/// <see cref="Commands.QuotaCost"/>), as last chosen with <c>--cache-read-weight</c>. <see langword="null"/> means the
/// default.
/// </param>
public sealed record LimitsSettings(
    DateTimeOffset? SessionResetAt,
    string? WeekResetAt,
    UsageCalibration? SessionCalibration = null,
    UsageCalibration? WeekCalibration = null,
    decimal? CacheReadWeight = null)
{
    /// <summary>
    /// An empty settings instance with no configured reset times or usage limits.
    /// </summary>
    public static readonly LimitsSettings Empty = new(null, null, null, null, null);

    /// <summary>
    /// Gets the session usage limit that an older version saved. It is only read, to tell the user it could not be
    /// carried over (a limit alone cannot be re-derived under another cache read weight), and is never written back.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? SessionCostLimit { get; init; }

    /// <summary>
    /// Gets the weekly usage limit that an older version saved; see <see cref="SessionCostLimit"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? WeekCostLimit { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file still holds a usage limit saved by an older version.
    /// </summary>
    [JsonIgnore]
    public bool HasLegacyLimits => SessionCostLimit is not null || WeekCostLimit is not null;
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
