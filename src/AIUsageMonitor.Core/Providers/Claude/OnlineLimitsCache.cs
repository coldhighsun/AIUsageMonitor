using AIUsageMonitor.Core.Models;
using System.Text.Json;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Keeps the last online quota reading on disk, so a start while the API is rate limited or unreachable still has
/// something to show. It holds the quotas, when they were read and a hash of the account they belong to, never a
/// token. A reading older than <see cref="MaxAge"/>, of another account, or from the future is not used, and a quota whose
/// window has reset since is left out because its share no longer says anything. Every file problem is a cache miss.
/// </summary>
/// <param name="path">The cache file.</param>
public sealed class OnlineLimitsCache(string path)
{
    /// <summary>The oldest reading that is still used.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets the default cache file, next to the session-row cache.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aimon", "cache", "online-limits.json");

    private static readonly Lock Sync = new();

    /// <summary>
    /// Loads the kept reading.
    /// </summary>
    /// <param name="account">The hash of the account signed in now.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The reading and when it was taken, or <see langword="null"/> when none is usable.</returns>
    public (OnlineLimits Limits, DateTimeOffset ReadAt)? Load(string account, DateTimeOffset now)
    {
        if (account.Length == 0)
        {
            return null;
        }

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
            if (stored is null || stored.Account != account || stored.ReadAt > now.AddMinutes(5) || now - stored.ReadAt > MaxAge)
            {
                return null;
            }

            OnlineQuota? Alive(OnlineQuota? quota) => quota is not null && double.IsFinite(quota.Percent) && (quota.ResetsAt is not { } reset || reset > now) ? quota : null;
            var session = Alive(stored.Limits?.Session);
            var week = Alive(stored.Limits?.Week);
            return session is null && week is null ? null : (new OnlineLimits(session, week), stored.ReadAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the kept reading. The content goes to a temporary file first and is then moved into place, so a crash never
    /// leaves half a file; a failed write keeps the previous file.
    /// </summary>
    /// <param name="account">The hash of the account the reading belongs to.</param>
    /// <param name="limits">The reading.</param>
    /// <param name="readAt">When it was taken.</param>
    public void Save(string account, OnlineLimits limits, DateTimeOffset readAt)
    {
        if (account.Length == 0)
        {
            return;
        }

        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(new OnlineLimitsCacheEntry(account, readAt, limits), CoreJsonContext.Default.OnlineLimitsCacheEntry));
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
    }

    private string PausePath => Path.ChangeExtension(path, ".pause.json");

    /// <summary>
    /// Keeps the end of a pause after a 429, so a restart does not ask again before it is over.
    /// </summary>
    /// <param name="account">The hash of the account the pause belongs to.</param>
    /// <param name="until">When the pause ends.</param>
    public void SavePause(string account, DateTimeOffset until)
    {
        if (account.Length == 0)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(PausePath, JsonSerializer.Serialize(new OnlineLimitsPause(account, until), CoreJsonContext.Default.OnlineLimitsPause));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The pause then only holds for this run.
        }
    }

    /// <summary>
    /// Loads the end of a pause an earlier run started.
    /// </summary>
    /// <param name="account">The hash of the account signed in now.</param>
    /// <param name="now">The current time.</param>
    /// <returns>When the pause ends, or <see langword="null"/> when none is running for this account.</returns>
    public DateTimeOffset? LoadPause(string account, DateTimeOffset now)
    {
        try
        {
            string json;
            lock (Sync)
            {
                if (account.Length == 0 || !File.Exists(PausePath))
                {
                    return null;
                }

                json = File.ReadAllText(PausePath);
            }

            var pause = JsonSerializer.Deserialize(json, CoreJsonContext.Default.OnlineLimitsPause);
            // A pause from the far future (a clock set back since) is not trusted beyond what a server may ask for.
            return pause is not null && pause.Account == account && pause.Until > now && pause.Until - now <= RateLimitBackoff.MaxPlausibleRetryAfter
                ? pause.Until
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Forgets a kept pause, as after a successful request.
    /// </summary>
    public void ClearPause()
    {
        try
        {
            lock (Sync)
            {
                File.Delete(PausePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Forgets the kept reading, as when the API rejected the sign-in.
    /// </summary>
    public void Delete()
    {
        try
        {
            lock (Sync)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be removed is still only a cache.
        }
    }
}
