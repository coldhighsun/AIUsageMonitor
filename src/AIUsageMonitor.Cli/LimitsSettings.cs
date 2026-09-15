using System.Text.Json;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Persisted user preferences for the <c>limits</c> command: the real reset times used to pin the
/// "Current session"/"This Week" windows to the account's own schedule.
/// </summary>
/// <param name="SessionResetAt">The real reset time of the current session window, if configured.</param>
/// <param name="WeekResetAt">The real weekly reset time, as originally typed (e.g. "Mon 09:00"), if configured.</param>
/// <param name="SessionTokenLimit">
/// The account's session (5h) token limit, in tokens. Claude itself only ever shows a usage
/// *percentage*, never the underlying limit, so this value is not typed in directly - it is derived
/// once (<c>currentTokens / (enteredPercent / 100)</c>) from the percentage the user read off
/// Claude's usage display and the token count this tool had counted for the window at that moment.
/// Once derived it is persisted and reused so the token-progress bar can keep tracking live as
/// tokens accrue, without asking the user again every refresh.
/// </param>
/// <param name="WeekTokenLimit">The account's weekly token limit, derived the same way as <see cref="SessionTokenLimit"/>.</param>
public sealed record LimitsSettings(
    DateTimeOffset? SessionResetAt,
    string? WeekResetAt,
    long? SessionTokenLimit = null,
    long? WeekTokenLimit = null)
{
    /// <summary>An empty settings instance with no configured reset times or token limits.</summary>
    public static readonly LimitsSettings Empty = new(null, null, null, null);
}

/// <summary>
/// Loads and saves <see cref="LimitsSettings"/> to a JSON file under the user's local application data folder.
/// </summary>
public static class LimitsSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "limits-settings.json");

    /// <summary>
    /// Loads the persisted <see cref="LimitsSettings"/>, or <see cref="LimitsSettings.Empty"/> if none exist
    /// or the file cannot be read.
    /// </summary>
    /// <returns>The loaded settings.</returns>
    public static LimitsSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return LimitsSettings.Empty;
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<LimitsSettings>(json) ?? LimitsSettings.Empty;
        }
        catch
        {
            return LimitsSettings.Empty;
        }
    }

    /// <summary>
    /// Saves the given <see cref="LimitsSettings"/>, creating the containing directory if needed.
    /// </summary>
    /// <param name="settings">The settings to persist.</param>
    public static void Save(LimitsSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings));
    }
}
