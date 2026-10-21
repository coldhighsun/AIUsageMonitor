using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Cli;

/// <summary>
/// One reading of Claude's own usage percentage together with the cost this tool had counted for the window at that
/// moment. The usage limit of the account is never shown by Claude, only a percentage, so the limit is derived from
/// such a reading - and it is derived on demand rather than stored, because the cost it divides depends on the cache
/// read weight (see <see cref="QuotaCost"/>). Keeping the reading lets the weight be tuned without reading the
/// percentage off Claude again.
/// </summary>
/// <param name="Breakdown">The estimated cost of the window by kind of token when the percentage was read.</param>
/// <param name="Percent">The usage percentage Claude showed at that moment, for example 32 for '32%'.</param>
public sealed record UsageCalibration(CostBreakdown Breakdown, double Percent)
{
    /// <summary>
    /// The smallest usage percentage that can be calibrated against. Anything lower rounds to zero when divided into a
    /// cost, so the limit could not be derived.
    /// </summary>
    public const double MinPercent = 0.01;

    /// <summary>
    /// The largest usage percentage that can be calibrated against. Claude can show a little over 100%; much more than that is a typo,
    /// and a value far beyond it would overflow the cost arithmetic.
    /// </summary>
    public const double MaxPercent = 100_000;

    /// <summary>
    /// Determines whether a usage percentage can be calibrated against. NaN and infinity are not.
    /// </summary>
    /// <param name="percent">The usage percentage, for example 32 for '32%'.</param>
    /// <returns><see langword="true"/> if the percentage lies between <see cref="MinPercent"/> and <see cref="MaxPercent"/>.</returns>
    public static bool IsValidPercent(double percent) => percent is >= MinPercent and <= MaxPercent;

    /// <summary>
    /// Drops a calibration that cannot be divided by, as a hand-edited settings file may hold one.
    /// </summary>
    /// <param name="calibration">The calibration read from the settings, if any.</param>
    /// <returns>The calibration if its percentage is valid (see <see cref="IsValidPercent"/>); otherwise <see langword="null"/>.</returns>
    public static UsageCalibration? OrNullIfInvalid(UsageCalibration? calibration)
        => calibration is not null && IsValidPercent(calibration.Percent) ? calibration : null;

    /// <summary>
    /// Derives the usage limit this reading implies under a cache read weight.
    /// </summary>
    /// <param name="cacheReadWeight">The share of the cache-read cost that counts toward the quota.</param>
    /// <returns>
    /// The limit, as a quota-weighted cost in US dollars (the cost at the reading divided by the percentage), or
    /// <see langword="null"/> when nothing counted at the reading under this weight (for example a reading taken right
    /// after a reset, or one made only of cache reads with a weight of 0) or the percentage is not usable, because then
    /// the reading says nothing about the size of the limit.
    /// </returns>
    public decimal? DeriveLimit(decimal cacheReadWeight)
    {
        var cost = QuotaCost.Compute(Breakdown, cacheReadWeight);

        return cost > 0m && IsValidPercent(Percent) ? cost / (decimal)(Percent / 100) : null;
    }

    /// <summary>
    /// Computes the percentage of the limit that some cost represents, which is what Claude would show for it.
    /// </summary>
    /// <param name="breakdown">The estimated cost of a window by kind of token.</param>
    /// <param name="cacheReadWeight">The share of the cache-read cost that counts toward the quota.</param>
    /// <returns>The usage percentage, for example 32 for '32%', or <see langword="null"/> when there is no limit to measure against.</returns>
    public decimal? PercentOf(CostBreakdown breakdown, decimal cacheReadWeight)
        => DeriveLimit(cacheReadWeight) is { } limit ? QuotaCost.Compute(breakdown, cacheReadWeight) * 100 / limit : null;
}
