using AIUsageMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Reads the usage samples the Claude desktop app keeps in <c>plan-usage-history.json</c> beside its configuration: periodic
/// readings of the signed-in account's 5-hour (<c>fh</c>) and 7-day (<c>sd</c>) utilization percentages. The file is local,
/// written by the app, and holds no credentials and no reset times; it is only updated while the app runs, so a reading
/// is as old as its sample time says. Nothing here touches the network or Claude Code's sign-in. Every problem (no app,
/// unreadable or malformed file) is simply no reading.
/// </summary>
/// <param name="profiles">
/// The desktop app's data folders; when <see langword="null"/>, the ones the app uses on Windows are looked up.
/// </param>
/// <param name="logger">The logger used to record a file that could not be read.</param>
public sealed class ClaudeDesktopUsageReader(IReadOnlyList<string>? profiles = null, ILogger<ClaudeDesktopUsageReader>? logger = null)
{
    /// <summary>The name of the samples file.</summary>
    public const string FileName = "plan-usage-history.json";

    /// <summary>A 5-hour sample older than the window itself says nothing about the current one.</summary>
    public static readonly TimeSpan SessionMaxAge = TimeSpan.FromHours(5);

    /// <summary>A weekly sample older than a week says nothing about the current one.</summary>
    public static readonly TimeSpan WeekMaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Finds the desktop app's data folders that exist on this machine.
    /// </summary>
    /// <returns>The folders, in the order the app's installs are most likely to be current.</returns>
    public static IReadOnlyList<string> DiscoverProfiles()
    {
        var result = new List<string>();
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (roaming.Length > 0)
        {
            result.Add(Path.Combine(roaming, "Claude"));
        }

        if (local.Length > 0)
        {
            foreach (var packages in SafeDirectories(Path.Combine(local, "Packages")))
            {
                var name = Path.GetFileName(packages);
                if (name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("AnthropicPBC.Claude_", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(Path.Combine(packages, "LocalCache", "Roaming", "Claude"));
                }
            }

            result.AddRange(SafeDirectories(Path.Combine(local, "Claude-Data")));
        }

        return result;
    }

    /// <summary>
    /// Gets the latest sample, as quotas that can still be true now.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The reading and when its sample was taken, or <see langword="null"/> when there is none that still applies.</returns>
    public (OnlineLimits Limits, DateTimeOffset ReadAt)? Read(DateTimeOffset now)
    {
        (double Session, double Week, DateTimeOffset Time)? latest = null;
        foreach (var profile in profiles ?? DiscoverProfiles())
        {
            try
            {
                var path = Path.Combine(profile, FileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("samples", out var samples)
                    || samples.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var sample in samples.EnumerateArray())
                {
                    if (ReadSample(sample) is { } read && read.Time <= now.AddMinutes(5) && (latest is null || read.Time > latest.Value.Time))
                    {
                        latest = read;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger?.LogDebug(ex, "Could not read the desktop usage samples in {Profile}", profile);
            }
        }

        if (latest is not { } sampleRead)
        {
            return null;
        }

        var age = now - sampleRead.Time;
        var session = age <= SessionMaxAge ? new OnlineQuota(sampleRead.Session, null) : null;
        var week = age <= WeekMaxAge ? new OnlineQuota(sampleRead.Week, null) : null;
        return session is null && week is null ? null : (new OnlineLimits(session, week), sampleRead.Time);
    }

    private static (double Session, double Week, DateTimeOffset Time)? ReadSample(JsonElement sample)
    {
        if (sample.ValueKind != JsonValueKind.Object
            || !sample.TryGetProperty("t", out var t)
            || ReadTime(t) is not { } time
            || !sample.TryGetProperty("u", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || ReadPercent(usage, "fh") is not { } session
            || ReadPercent(usage, "sd") is not { } week)
        {
            return null;
        }

        return (session, week, time);
    }

    private static double? ReadPercent(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var percent)
            && double.IsFinite(percent) && percent >= 0
                ? Math.Min(percent, 100)
                : null;

    private static DateTimeOffset? ReadTime(JsonElement t)
    {
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var millis))
        {
            // Milliseconds since the Unix epoch; anything outside the range a DateTimeOffset holds is not a time.
            return millis is > 0 and < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : null;
        }

        return t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var parsed) ? parsed : null;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
