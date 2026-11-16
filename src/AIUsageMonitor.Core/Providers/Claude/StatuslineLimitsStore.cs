using AIUsageMonitor.Core.Models;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Keeps the quotas Claude Code itself hands to its status line command: the JSON on that command's standard input has a
/// <c>rate_limits</c> object with the 5-hour (<c>five_hour</c>) and 7-day (<c>seven_day</c>) <c>used_percentage</c> and
/// <c>resets_at</c> (Unix seconds). <see cref="Record"/> parses it and keeps the quotas in a small local file; <see cref="Read"/>
/// gives them back while they can still be true. Nothing here touches the network or Claude Code's sign-in, and the file holds
/// only percentages and times. Every problem (unwritable or malformed file, input without quotas) is simply no reading.
/// </summary>
/// <param name="path">The file the quotas are kept in.</param>
public sealed class StatuslineLimitsStore(string path)
{
    /// <summary>A reading older than this is no longer taken as current: the status line only runs while a session is.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private static readonly Lock Sync = new();

    /// <summary>
    /// Gets the default file, next to the other caches.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "cache", "statusline-limits.json");

    /// <summary>
    /// Parses the quotas out of what Claude Code gives its status line command.
    /// </summary>
    /// <param name="statuslineJson">The JSON read from the command's standard input.</param>
    /// <param name="now">The current time; a window whose reset time has passed is left out.</param>
    /// <returns>The quotas, or <see langword="null"/> when the input has none.</returns>
    public static OnlineLimits? Parse(string statuslineJson, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(statuslineJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var session = ReadWindow(limits, "five_hour", now);
            var week = ReadWindow(limits, "seven_day", now);
            return session is null && week is null ? null : new OnlineLimits(session, week);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the status line input and, when it has quotas, keeps them. A failed write keeps the previous file.
    /// </summary>
    /// <param name="statuslineJson">The JSON read from the status line command's standard input.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The quotas that were kept, or <see langword="null"/> when the input had none.</returns>
    public OnlineLimits? Record(string statuslineJson, DateTimeOffset now)
    {
        if (Parse(statuslineJson, now) is not { } limits)
        {
            return null;
        }

        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(new OnlineLimitsCacheEntry(string.Empty, now, limits), CoreJsonContext.Default.OnlineLimitsCacheEntry));
                File.Move(temporary, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        return limits;
    }

    /// <summary>
    /// Gets the kept quotas that can still be true now.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The quotas and when they were recorded, or <see langword="null"/> when none apply.</returns>
    public (OnlineLimits Limits, DateTimeOffset ReadAt)? Read(DateTimeOffset now)
    {
        try
        {
            string json;
            lock (Sync)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                json = File.ReadAllText(path);
            }

            var stored = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OnlineLimitsCacheEntry);
            if (stored is null || stored.ReadAt > now.AddMinutes(5) || now - stored.ReadAt > MaxAge)
            {
                return null;
            }

            OnlineQuota? Alive(OnlineQuota? quota)
                => quota is not null && double.IsFinite(quota.Percent) && quota.Percent >= 0 && (quota.ResetsAt is not { } reset || reset > now) ? quota : null;

            var session = Alive(stored.Limits?.Session);
            var week = Alive(stored.Limits?.Week);
            return session is null && week is null ? null : (new OnlineLimits(session, week), stored.ReadAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static OnlineQuota? ReadWindow(JsonElement limits, string name, DateTimeOffset now)
    {
        if (!limits.TryGetProperty(name, out var window)
            || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("used_percentage", out var used)
            || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent)
            || !double.IsFinite(percent) || percent < 0)
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds)
            && seconds is > 0 and < 253_402_300_799)
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        // A window that has reset says nothing about the new one.
        return resetsAt is { } at && at <= now ? null : new OnlineQuota(Math.Min(percent, 100), resetsAt);
    }
}
