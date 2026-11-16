namespace AIUsageMonitor.Core.Models;

/// <summary>
/// One usage quota as Anthropic's account API reports it.
/// </summary>
/// <param name="Percent">The share of the quota used, 0 to 100 (it may read a little over 100).</param>
/// <param name="ResetsAt">When the quota resets, if the API said.</param>
public sealed record OnlineQuota(double Percent, DateTimeOffset? ResetsAt);

/// <summary>
/// The exact quotas Anthropic's account API reports for the signed-in account.
/// </summary>
/// <param name="Session">The current 5-hour session quota, if reported.</param>
/// <param name="Week">The weekly quota (all models), if reported.</param>
public sealed record OnlineLimits(OnlineQuota? Session, OnlineQuota? Week);

/// <summary>
/// Why no live online reading is available.
/// </summary>
public enum OnlineLimitsFailure
{
    /// <summary>There was no failure: the reading is live.</summary>
    None,

    /// <summary>Online reading was switched off by the caller.</summary>
    Disabled,

    /// <summary>Claude Code's sign-in could not be found.</summary>
    NotSignedIn,

    /// <summary>The API rejected the sign-in (401 or 403).</summary>
    Rejected,

    /// <summary>The API asked for a pause (429), or a pause from an earlier 429 is still running.</summary>
    RateLimited,

    /// <summary>The network failed or the answer could not be understood.</summary>
    Unavailable,
}

/// <summary>
/// The outcome of asking for the online quotas: a reading and where it came from, or the reason there is none.
/// </summary>
/// <param name="Limits">The reading, or <see langword="null"/> when there is none.</param>
/// <param name="ReadAt">When the reading was taken from the API, or <see langword="null"/> when there is none.</param>
/// <param name="FromCache">Whether the reading is an earlier one kept on disk, shown because a live one could not be had.</param>
/// <param name="Failure">Why there is no live reading; <see cref="OnlineLimitsFailure.None"/> when the reading is live.</param>
/// <param name="RetryAt">When the next request may go out, when a pause is running.</param>
/// <param name="FromDesktopApp">
/// Whether the percentages are the latest sample the Claude desktop app recorded on this machine (see
/// <see cref="Providers.Claude.ClaudeDesktopUsageReader"/>), as old as <paramref name="ReadAt"/> says.
/// </param>
/// <param name="FromStatusline">
/// Whether the percentages are those Claude Code itself last gave its status line command (see
/// <see cref="Providers.Claude.StatuslineLimitsStore"/>), with the reset times it reported; they are live when recent.
/// </param>
public sealed record OnlineLimitsResult(
    OnlineLimits? Limits,
    DateTimeOffset? ReadAt,
    bool FromCache,
    OnlineLimitsFailure Failure,
    DateTimeOffset? RetryAt = null,
    bool FromDesktopApp = false,
    bool FromStatusline = false)
{
    /// <summary>Gets a value indicating whether the reading is live (just asked of the API, or still fresh).</summary>
    public bool IsLive => Limits is not null && !FromCache && Failure == OnlineLimitsFailure.None;

    /// <summary>The result when online reading is switched off.</summary>
    public static OnlineLimitsResult Disabled { get; } = new(null, null, false, OnlineLimitsFailure.Disabled);
}

/// <summary>
/// What <see cref="Providers.Claude.OnlineLimitsCache"/> keeps on disk: the reading, when it was taken and a hash of its account.
/// </summary>
/// <param name="Account">The one-way hash of the account the reading belongs to.</param>
/// <param name="ReadAt">When the reading was taken from the API.</param>
/// <param name="Limits">The reading.</param>
public sealed record OnlineLimitsCacheEntry(string Account, DateTimeOffset ReadAt, OnlineLimits Limits);

/// <summary>
/// The end of a pause after a 429, kept on disk so a restart does not ask again before it is over.
/// </summary>
/// <param name="Account">The one-way hash of the account the pause belongs to.</param>
/// <param name="Until">When the pause ends.</param>
public sealed record OnlineLimitsPause(string Account, DateTimeOffset Until);
