namespace AIUsageMonitor.Core.Providers.Claude;

/// <summary>
/// The pause after a 429 from the quota API: it is not asked again until the pause ends (a manual refresh included).
/// The wait is the server's <c>Retry-After</c> when it gave a plausible one, otherwise 2, 4, 8, 16 minutes and so on up
/// to 30, plus a little jitter. Not thread safe; the caller serializes access.
/// </summary>
public sealed class RateLimitBackoff
{
    /// <summary>The shortest wait, even when the server asked for less.</summary>
    public static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(30);

    /// <summary>The wait after the first failure when the server named none.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    /// <summary>The longest the wait grows to without a server-given one.</summary>
    public static readonly TimeSpan MaxGrowth = TimeSpan.FromMinutes(30);

    /// <summary>A <c>Retry-After</c> longer than this is taken as invalid.</summary>
    public static readonly TimeSpan MaxPlausibleRetryAfter = TimeSpan.FromDays(7);

    /// <summary>The share of the wait that jitter may add.</summary>
    public const double JitterShare = 0.1;

    private const int MaxFailures = 16;

    private DateTimeOffset? _until;

    /// <summary>Gets the number of failures in a row.</summary>
    public int Failures { get; private set; }

    /// <summary>
    /// Computes the wait after a failure.
    /// </summary>
    /// <param name="failures">The number of failures in a row, this one included.</param>
    /// <param name="retryAfter">The wait the server asked for, if any.</param>
    /// <param name="jitter">A number from 0 to 1 that scales the jitter.</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Delay(int failures, TimeSpan? retryAfter, double jitter)
    {
        var grown = failures <= 1
            ? FirstDelay
            : TimeSpan.FromTicks((long)Math.Min(FirstDelay.Ticks * Math.Pow(2, Math.Min(failures, MaxFailures) - 1), MaxGrowth.Ticks));
        var delay = grown;
        if (retryAfter is { } told && told > TimeSpan.Zero && told <= MaxPlausibleRetryAfter)
        {
            delay = told < MinDelay ? MinDelay : told;
            if (failures > 1 && grown > delay)
            {
                delay = grown;
            }
        }

        return delay + (delay * (JitterShare * Math.Clamp(double.IsNaN(jitter) ? 0 : jitter, 0, 1)));
    }

    /// <summary>
    /// Says when requests may go out again.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The end of the pause, or <see langword="null"/> when a request may go out now.</returns>
    public DateTimeOffset? RetryAt(DateTimeOffset now) => _until is { } until && until > now ? until : null;

    /// <summary>
    /// Starts or extends the pause after a 429.
    /// </summary>
    /// <param name="now">The time the 429 arrived.</param>
    /// <param name="retryAfter">The wait the server asked for, if any.</param>
    /// <param name="jitter">A number from 0 to 1 that scales the jitter.</param>
    /// <returns>When requests may go out again.</returns>
    public DateTimeOffset Fail(DateTimeOffset now, TimeSpan? retryAfter, double jitter)
    {
        Failures = Math.Min(Failures + 1, MaxFailures);
        return (_until = now + Delay(Failures, retryAfter, jitter)).Value;
    }

    /// <summary>
    /// Takes up a pause that an earlier run started, so a restart does not ask before it ends.
    /// </summary>
    /// <param name="until">When the earlier pause ends.</param>
    public void Restore(DateTimeOffset until)
    {
        Failures = Math.Max(Failures, 1);
        _until = until;
    }

    /// <summary>
    /// Ends the pause after a successful request.
    /// </summary>
    public void Reset()
    {
        Failures = 0;
        _until = null;
    }
}
