using System.Text.Json;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Persisted user preferences for the <c>limits</c> command: the anchors used to pin the
/// "Current session"/"This Week" windows to the account's real reset times.
/// </summary>
/// <param name="SessionAnchor">The real current-session window start time, if configured.</param>
/// <param name="WeekAnchor">The real weekly reset anchor, as originally typed (e.g. "Mon 09:00"), if configured.</param>
public sealed record LimitsSettings(
    DateTimeOffset? SessionAnchor,
    string? WeekAnchor)
{
    /// <summary>An empty settings instance with no configured anchors.</summary>
    public static readonly LimitsSettings Empty = new(null, null);
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
