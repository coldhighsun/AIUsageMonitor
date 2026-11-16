using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// Gives the exact quotas of the signed-in account: it asks the account API through <see cref="ClaudeOAuthUsageClient"/>
/// at most once per <see cref="MinInterval"/>, pauses after a 429 (<see cref="RateLimitBackoff"/>), and falls back to the
/// reading kept on disk (<see cref="OnlineLimitsCache"/>) and the latest sample of the Claude desktop app
/// (<see cref="ClaudeDesktopUsageReader"/>, local and possibly older) when a live one cannot be had. It never invents a figure.
/// </summary>
/// <param name="client">Asks the API.</param>
/// <param name="cache">Keeps the last reading across runs.</param>
/// <param name="timeProvider">The clock; defaults to the system clock.</param>
/// <param name="jitter">Produces the 0 to 1 jitter of a pause; defaults to a random one.</param>
/// <param name="desktop">Reads the desktop app's samples; when omitted, there is no such fallback.</param>
/// <param name="statusline">
/// Reads the quotas Claude Code gave its status line command (recorded by <c>aimon statusline</c>); when omitted, there is none.
/// A recent one is used as the live reading without asking the API at all.
/// </param>
public sealed class ClaudeOnlineLimitsService(
    ClaudeOAuthUsageClient client,
    OnlineLimitsCache cache,
    TimeProvider? timeProvider = null,
    Func<double>? jitter = null,
    ClaudeDesktopUsageReader? desktop = null,
    StatuslineLimitsStore? statusline = null)
{
    /// <summary>
    /// The shortest time between two requests; a call within it is answered from the last reading.
    /// </summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How recent the quotas from Claude Code's status line must be to count as live: it refreshes them after every response of
    /// a running session, so one this young is what Claude Code itself shows.
    /// </summary>
    public static readonly TimeSpan StatuslineFreshness = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;
    private readonly RateLimitBackoff _backoff = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OnlineLimitsResult? _last;
    private DateTimeOffset _lastRequestAt;
    private string? _account;

    /// <summary>
    /// Gets the quotas: a live reading when one can be had, otherwise the kept one with the reason there is no live one.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The reading and where it came from, or why there is none.</returns>
    public async Task<OnlineLimitsResult> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_account is null)
            {
                _account = client.GetAccountHash();

                // A pause an earlier run started still holds: asking again before it ends only makes the server extend it.
                if (cache.LoadPause(_account, now) is { } earlier)
                {
                    _backoff.Restore(earlier);
                }
            }

            // Claude Code's own recent figures need no request, so a running session never meets the API's rate limit.
            if (statusline?.Read(now) is { } fresh && now - fresh.ReadAt <= StatuslineFreshness)
            {
                return new(fresh.Limits, fresh.ReadAt, false, OnlineLimitsFailure.None, FromStatusline: true);
            }

            if (_last is { IsLive: true } live && now - _lastRequestAt < MinInterval && now >= _lastRequestAt)
            {
                return live;
            }

            if (_backoff.RetryAt(now) is { } wait)
            {
                return Fallback(now, OnlineLimitsFailure.RateLimited, wait);
            }

            _lastRequestAt = now;
            var outcome = await client.FetchAsync(ct).ConfigureAwait(false);
            now = _time.GetUtcNow();

            if (outcome.Limits is { } limits)
            {
                _backoff.Reset();
                cache.ClearPause();
                cache.Save(_account, limits, now);
                return _last = new(limits, now, false, OnlineLimitsFailure.None);
            }

            DateTimeOffset? retryAt = null;
            switch (outcome.Failure)
            {
                case OnlineLimitsFailure.RateLimited:
                    retryAt = _backoff.Fail(now, outcome.RetryAfter, _jitter());
                    cache.SavePause(_account, retryAt.Value);
                    break;
                case OnlineLimitsFailure.Rejected:
                    // The sign-in no longer works: what was read with it says nothing now.
                    cache.Delete();
                    _last = null;
                    return new(null, null, false, OnlineLimitsFailure.Rejected);
            }

            return Fallback(now, outcome.Failure, retryAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gets what the desktop app's samples say, without reading Claude Code's sign-in or touching the network; this is what
    /// remains when online reading is switched off.
    /// </summary>
    /// <returns>A result that is never live: the sample (as old as its time says), or no reading.</returns>
    public OnlineLimitsResult GetLocalOnly()
    {
        var now = _time.GetUtcNow();
        var fromStatusline = statusline?.Read(now);
        var fromDesktop = desktop?.Read(now);
        if (fromStatusline is { } line && (fromDesktop is not { } app || line.ReadAt >= app.ReadAt))
        {
            return new(line.Limits, line.ReadAt, true, OnlineLimitsFailure.Disabled, null, FromStatusline: true);
        }

        return fromDesktop is { } sample
            ? new(sample.Limits, sample.ReadAt, true, OnlineLimitsFailure.Disabled, null, FromDesktopApp: true)
            : OnlineLimitsResult.Disabled;
    }

    private OnlineLimitsResult Fallback(DateTimeOffset now, OnlineLimitsFailure failure, DateTimeOffset? retryAt)
    {
        // What the account API said last: the reading of this run first, as it is at least as new as the one on disk.
        (OnlineLimits Limits, DateTimeOffset ReadAt)? api = null;
        if (_last?.Limits is { } held && _last.ReadAt is { } heldAt && Alive(held, now) is { } alive)
        {
            api = (alive, heldAt);
        }
        else if (_account is { Length: > 0 } account && cache.Load(account, now) is { } kept)
        {
            api = kept;
        }

        var sample = desktop?.Read(now);

        // Claude Code's own figures, older than fresh but newer than everything else, carry their own reset times.
        if (statusline?.Read(now) is { } line
            && (api is not { } apiKnown || line.ReadAt > apiKnown.ReadAt)
            && (sample is not { } sampleKnown || line.ReadAt >= sampleKnown.ReadAt))
        {
            return new(line.Limits, line.ReadAt, true, failure, retryAt, FromStatusline: true);
        }

        if (sample is { } local && (api is not { } known || local.ReadAt > known.ReadAt))
        {
            // The desktop app's percentages are newer. They carry no reset times, so those come from the API reading when it
            // still has them (a reset time that has not passed means the same window, so the percentage belongs to it).
            var merged = new OnlineLimits(
                local.Limits.Session is { } s ? s with { ResetsAt = api?.Limits.Session?.ResetsAt } : null,
                local.Limits.Week is { } w ? w with { ResetsAt = api?.Limits.Week?.ResetsAt } : null);
            return new(merged, local.ReadAt, true, failure, retryAt, FromDesktopApp: true);
        }

        return api is { } reading
            ? new(reading.Limits, reading.ReadAt, true, failure, retryAt)
            : new(null, null, false, failure, retryAt);
    }

    private static OnlineLimits? Alive(OnlineLimits limits, DateTimeOffset now)
    {
        var session = limits.Session is { } s && (s.ResetsAt is not { } sr || sr > now) ? s : null;
        var week = limits.Week is { } w && (w.ResetsAt is not { } wr || wr > now) ? w : null;
        return session is null && week is null ? null : new(session, week);
    }
}
