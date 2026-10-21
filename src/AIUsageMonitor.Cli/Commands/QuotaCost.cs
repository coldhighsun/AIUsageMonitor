using AIUsageMonitor.Core.Models;
using System.CommandLine;
using System.Globalization;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// The cost figure the usage-progress bar of <c>watch --view limits</c> is measured in. It is the estimated API cost
/// with the cache reads scaled down: the usage quota of a subscription is not metered at API list prices, and cache
/// reads are far more numerous than any other kind of token in an agentic session (the whole context is read again on
/// every turn), so counting them at their full list price makes the local bar run ahead of the percentage Claude shows.
/// </summary>
internal static class QuotaCost
{
    /// <summary>
    /// The default share of the list price of a cache read that counts toward the quota. This is a heuristic, not a
    /// measured value: Anthropic does not publish how the quota weighs the kinds of token, so it can be tuned with
    /// <c>--cache-read-weight</c> by comparing the local bar with Claude's own percentage.
    /// </summary>
    internal const decimal DefaultCacheReadWeight = 0.1m;

    /// <summary>
    /// Creates the <c>--cache-read-weight</c> option, which accepts a value from 0 to 1.
    /// </summary>
    /// <returns>The option.</returns>
    internal static Option<decimal?> CreateOption()
    {
        var option = new Option<decimal?>("--cache-read-weight")
        {
            Description = $"Share (0 to 1, with '.' as the decimal separator) of the list price of cache-read tokens that counts toward the usage quota, default {DefaultCacheReadWeight} (a heuristic, not a published figure). Lower it if the usage-progress bar runs ahead of Claude's own percentage, raise it if it runs behind; the saved usage percentages are kept and the limits are derived again under the new weight. Only used when --view limits.",
            CustomParser = result =>
            {
                if (decimal.TryParse(result.Tokens.Single().Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                    && IsValidWeight(value))
                {
                    return value;
                }

                result.AddError("--cache-read-weight must be a number between 0 and 1.");
                return null;
            }
        };
        return option;
    }

    /// <summary>
    /// Creates the <c>--reset-cache-read-weight</c> flag, which forgets the saved weight.
    /// </summary>
    /// <returns>The option.</returns>
    internal static Option<bool> CreateResetOption()
    {
        return new Option<bool>("--reset-cache-read-weight")
        {
            Description = $"Forget the saved --cache-read-weight and use the default ({DefaultCacheReadWeight}) again, including in future runs. Cannot be combined with --cache-read-weight. Only used when --view limits."
        };
    }

    /// <summary>
    /// Makes parsing fail when both the weight and its reset are given, before the command does anything.
    /// </summary>
    /// <param name="command">The command that has both options.</param>
    /// <param name="weightOption">The <c>--cache-read-weight</c> option.</param>
    /// <param name="resetOption">The <c>--reset-cache-read-weight</c> option.</param>
    internal static void AddConflictValidator(Command command, Option<decimal?> weightOption, Option<bool> resetOption)
    {
        command.Validators.Add(result =>
        {
            if (result.GetResult(weightOption) is not null && result.GetResult(resetOption) is not null)
            {
                result.AddError("--reset-cache-read-weight cannot be combined with --cache-read-weight.");
            }
        });
    }

    /// <summary>
    /// Determines whether a cache read weight is allowed.
    /// </summary>
    /// <param name="weight">The weight to check.</param>
    /// <returns><see langword="true"/> if the weight lies between 0 and 1, inclusive.</returns>
    internal static bool IsValidWeight(decimal weight) => weight is >= 0m and <= 1m;

    /// <summary>
    /// Resolves the weight in effect: the command-line argument, else the persisted one, else the default. A persisted
    /// weight that is not valid (the file was edited by hand) is ignored.
    /// </summary>
    /// <param name="argument">The <c>--cache-read-weight</c> argument, if given.</param>
    /// <param name="saved">The persisted weight, if any.</param>
    /// <param name="reset">Whether <c>--reset-cache-read-weight</c> was given, which makes the default apply.</param>
    /// <returns>The cache read weight to use.</returns>
    internal static decimal ResolveWeight(decimal? argument, decimal? saved, bool reset = false)
        => WeightToSave(argument, saved, reset) is { } value && IsValidWeight(value) ? value : DefaultCacheReadWeight;

    /// <summary>
    /// Decides which weight to persist: only a weight the user chose is kept, so that the default can change in a
    /// later version, and resetting forgets it.
    /// </summary>
    /// <param name="argument">The <c>--cache-read-weight</c> argument, if given.</param>
    /// <param name="saved">The persisted weight, if any.</param>
    /// <param name="reset">Whether <c>--reset-cache-read-weight</c> was given.</param>
    /// <returns>
    /// The weight to save, or <see langword="null"/> for none (the default applies). A saved weight that is not valid is
    /// not kept, since it is ignored when resolving the weight.
    /// </returns>
    internal static decimal? WeightToSave(decimal? argument, decimal? saved, bool reset)
        => reset ? null : argument ?? (saved is { } value && IsValidWeight(value) ? value : null);

    /// <summary>
    /// Computes the cost that counts toward the quota.
    /// </summary>
    /// <param name="breakdown">The estimated cost of a window by kind of token.</param>
    /// <param name="cacheReadWeight">The share of the cache-read cost that counts.</param>
    /// <returns>The weighted cost, in US dollars.</returns>
    internal static decimal Compute(CostBreakdown breakdown, decimal cacheReadWeight)
        => Weighted(breakdown, cacheReadWeight).Total;

    /// <summary>
    /// Scales the cache-read part of a cost breakdown by the weight, leaving the other kinds as they are. This is
    /// the one place that decides which kinds count and how much.
    /// </summary>
    /// <param name="breakdown">The estimated cost of a window by kind of token.</param>
    /// <param name="cacheReadWeight">The share of the cache-read cost that counts.</param>
    /// <returns>The breakdown of the cost that counts toward the quota.</returns>
    internal static CostBreakdown Weighted(CostBreakdown breakdown, decimal cacheReadWeight)
        => breakdown with { CacheRead = breakdown.CacheRead * cacheReadWeight };

    /// <summary>
    /// Computes how much of the weighted cost each kind of token accounts for.
    /// </summary>
    /// <param name="breakdown">The estimated cost of a window by kind of token.</param>
    /// <param name="cacheReadWeight">The share of the cache-read cost that counts.</param>
    /// <returns>
    /// The share of each kind, or <see langword="null"/> when nothing counts toward the quota.
    /// </returns>
    internal static CostShares? Shares(CostBreakdown breakdown, decimal cacheReadWeight)
    {
        var weighted = Weighted(breakdown, cacheReadWeight);
        var total = weighted.Total;
        if (total <= 0m)
        {
            return null;
        }

        return new(weighted.Input / total, weighted.Output / total, weighted.CacheRead / total, weighted.CacheWrite / total);
    }
}
