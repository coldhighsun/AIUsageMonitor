using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Models;
using System.CommandLine;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="QuotaCost"/>, the weighted cost the usage-progress bar is measured in.
/// </summary>
public class QuotaCostTests
{
    /// <summary>
    /// A breakdown with a different cost for every kind of token, so a mix-up between them changes the result.
    /// </summary>
    private static readonly CostBreakdown Sample = new(Input: 1m, Output: 2m, CacheRead: 10m, CacheWrite: 3m);

    /// <summary>
    /// Verifies that a weight of 1 reproduces the plain estimated cost.
    /// </summary>
    [Fact]
    public void Compute_WeightOfOne_EqualsTheTotalCost()
    {
        var cost = QuotaCost.Compute(Sample, 1m);

        Assert.Equal(Sample.Total, cost);
    }

    /// <summary>
    /// Verifies that only the cache reads are scaled by the weight.
    /// </summary>
    /// <param name="weight">The cache read weight.</param>
    /// <param name="expected">The expected weighted cost.</param>
    [Theory]
    [InlineData(0, 6)]
    [InlineData(0.1, 7)]
    [InlineData(0.5, 11)]
    public void Compute_PartialWeight_ScalesOnlyTheCacheReads(double weight, int expected)
    {
        var cost = QuotaCost.Compute(Sample, (decimal)weight);

        Assert.Equal(expected, cost);
    }

    /// <summary>
    /// Verifies which weights are accepted.
    /// </summary>
    /// <param name="weight">The weight to check.</param>
    /// <param name="expected">Whether it is valid.</param>
    [Theory]
    [InlineData(0, true)]
    [InlineData(0.1, true)]
    [InlineData(1, true)]
    [InlineData(-0.1, false)]
    [InlineData(1.5, false)]
    public void IsValidWeight_AnyValue_AcceptsOnlyZeroToOne(double weight, bool expected)
    {
        var valid = QuotaCost.IsValidWeight((decimal)weight);

        Assert.Equal(expected, valid);
    }

    /// <summary>
    /// Verifies that the argument wins over the persisted weight, which wins over the default.
    /// </summary>
    [Fact]
    public void ResolveWeight_ArgumentSavedAndNeither_PicksInThatOrder()
    {
        Assert.Equal(0.3m, QuotaCost.ResolveWeight(0.3m, 0.2m));
        Assert.Equal(0.2m, QuotaCost.ResolveWeight(null, 0.2m));
        Assert.Equal(QuotaCost.DefaultCacheReadWeight, QuotaCost.ResolveWeight(null, null));
    }

    /// <summary>
    /// Verifies that a persisted weight that was edited into an invalid value is ignored.
    /// </summary>
    [Fact]
    public void ResolveWeight_InvalidSavedWeight_FallsBackToTheDefault()
    {
        var weight = QuotaCost.ResolveWeight(null, 7m);

        Assert.Equal(QuotaCost.DefaultCacheReadWeight, weight);
    }

    /// <summary>
    /// Verifies that the shares of the kinds are fractions of the weighted cost and add up to the whole.
    /// </summary>
    [Fact]
    public void Shares_AnyBreakdown_SumToOne()
    {
        var shares = QuotaCost.Shares(Sample, 0.1m);

        Assert.NotNull(shares);
        Assert.Equal(1m / 7, shares.Value.CacheRead);
        Assert.Equal(2m / 7, shares.Value.Output);
        Assert.Equal(3m / 7, shares.Value.CacheWrite);
        Assert.Equal(1m / 7, shares.Value.Input);
        Assert.Equal(1m, shares.Value.Input + shares.Value.Output + shares.Value.CacheRead + shares.Value.CacheWrite, precision: 9);
    }

    /// <summary>
    /// Verifies that an empty window has no shares, so nothing is divided by zero.
    /// </summary>
    [Fact]
    public void Shares_NothingCounts_ReturnsNull()
    {
        Assert.Null(QuotaCost.Shares(default, 0.1m));
        Assert.Null(QuotaCost.Shares(new CostBreakdown(0m, 0m, 5m, 0m), 0m));
    }

    /// <summary>
    /// Verifies that only the cache-read part of the breakdown is scaled.
    /// </summary>
    [Fact]
    public void Weighted_PartialWeight_ScalesOnlyTheCacheReadPart()
    {
        var weighted = QuotaCost.Weighted(Sample, 0.1m);

        Assert.Equal(new CostBreakdown(Input: 1m, Output: 2m, CacheRead: 1m, CacheWrite: 3m), weighted);
    }

    /// <summary>
    /// Parses arguments against a command that only has the cache read weight option.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parse result and the option, to read the value back.</returns>
    private static (ParseResult Result, Option<decimal?> Option) ParseOption(params string[] args)
    {
        var option = QuotaCost.CreateOption();
        var command = new Command("watch");
        command.Options.Add(option);

        return (command.Parse(args), option);
    }

    /// <summary>
    /// Verifies that a weight from 0 to 1 is parsed.
    /// </summary>
    /// <param name="text">The argument text.</param>
    /// <param name="expected">The expected weight.</param>
    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.25", 0.25)]
    [InlineData("1", 1)]
    public void CreateOption_WeightInRange_ParsesWithoutErrors(string text, double expected)
    {
        var (result, option) = ParseOption("--cache-read-weight", text);

        Assert.Empty(result.Errors);
        Assert.Equal((decimal)expected, result.GetValue(option));
    }

    /// <summary>
    /// Verifies that a weight outside 0 to 1, or text that is not a number, is rejected.
    /// </summary>
    /// <param name="text">The argument text.</param>
    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("abc")]
    public void CreateOption_WeightOutOfRangeOrNotANumber_ReportsAnError(string text)
    {
        var (result, _) = ParseOption("--cache-read-weight", text);

        Assert.NotEmpty(result.Errors);
    }

    /// <summary>
    /// Verifies that leaving the option out gives no value, so the saved weight or the default applies.
    /// </summary>
    [Fact]
    public void CreateOption_OptionOmitted_HasNoValue()
    {
        var (result, option) = ParseOption();

        Assert.Empty(result.Errors);
        Assert.Null(result.GetValue(option));
    }

    /// <summary>
    /// Verifies that resetting makes the default apply even when a weight was saved or given.
    /// </summary>
    [Fact]
    public void ResolveWeight_Reset_UsesTheDefault()
    {
        Assert.Equal(QuotaCost.DefaultCacheReadWeight, QuotaCost.ResolveWeight(null, 0.3m, reset: true));
        Assert.Equal(QuotaCost.DefaultCacheReadWeight, QuotaCost.ResolveWeight(0.4m, 0.3m, reset: true));
    }

    /// <summary>
    /// Verifies that only a weight the user chose is persisted: the argument, else the saved one, and nothing after a reset.
    /// </summary>
    [Fact]
    public void WeightToSave_ArgumentSavedAndReset_KeepsOnlyWhatTheUserChose()
    {
        Assert.Equal(0.4m, QuotaCost.WeightToSave(0.4m, 0.3m, reset: false));
        Assert.Equal(0.3m, QuotaCost.WeightToSave(null, 0.3m, reset: false));
        Assert.Null(QuotaCost.WeightToSave(null, null, reset: false));
        Assert.Null(QuotaCost.WeightToSave(null, 0.3m, reset: true));
    }

    /// <summary>
    /// Verifies that a saved weight that is not valid is not kept, since resolving the weight ignores it.
    /// </summary>
    [Fact]
    public void WeightToSave_InvalidSavedWeight_IsNotKept()
    {
        Assert.Null(QuotaCost.WeightToSave(null, 7m, reset: false));
        Assert.Equal(0.4m, QuotaCost.WeightToSave(0.4m, 7m, reset: false));
    }

    /// <summary>
    /// Verifies that the reset flag is off unless it is given.
    /// </summary>
    [Fact]
    public void CreateResetOption_GivenOrOmitted_IsTrueOnlyWhenGiven()
    {
        var option = QuotaCost.CreateResetOption();
        var command = new Command("watch");
        command.Options.Add(option);

        Assert.True(command.Parse("--reset-cache-read-weight").GetValue(option));
        Assert.False(command.Parse(Array.Empty<string>()).GetValue(option));
    }

    /// <summary>
    /// Builds a command with both weight options and the conflict validator.
    /// </summary>
    /// <returns>The command and its two options.</returns>
    private static (Command Command, Option<decimal?> Weight, Option<bool> Reset) CommandWithBothOptions()
    {
        var weight = QuotaCost.CreateOption();
        var reset = QuotaCost.CreateResetOption();
        var command = new Command("watch");
        command.Options.Add(weight);
        command.Options.Add(reset);
        QuotaCost.AddConflictValidator(command, weight, reset);

        return (command, weight, reset);
    }

    /// <summary>
    /// Verifies that giving the weight and its reset together fails at parse time.
    /// </summary>
    [Fact]
    public void AddConflictValidator_BothOptionsGiven_ReportsAnError()
    {
        var (command, _, _) = CommandWithBothOptions();

        var result = command.Parse(["--cache-read-weight", "0.2", "--reset-cache-read-weight"]);

        Assert.Contains(result.Errors, error => error.Message.Contains("cannot be combined"));
    }

    /// <summary>
    /// Verifies that either option alone, or neither, is accepted.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    [Theory]
    [InlineData("--cache-read-weight", "0.2")]
    [InlineData("--reset-cache-read-weight")]
    public void AddConflictValidator_OneOrNoOption_ReportsNoError(params string[] args)
    {
        var (command, _, _) = CommandWithBothOptions();

        Assert.Empty(command.Parse(args).Errors);
        Assert.Empty(command.Parse(Array.Empty<string>()).Errors);
    }
}
