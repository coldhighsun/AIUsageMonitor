namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// How much of a quota-weighted cost each kind of token accounts for. The figures are fractions of 1, not dollars.
/// </summary>
/// <param name="Input">The share of the uncached input tokens.</param>
/// <param name="Output">The share of the output tokens, including thinking.</param>
/// <param name="CacheRead">The share of the tokens read from the prompt cache, after weighting.</param>
/// <param name="CacheWrite">The share of the tokens written to the prompt cache.</param>
internal readonly record struct CostShares(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite);
