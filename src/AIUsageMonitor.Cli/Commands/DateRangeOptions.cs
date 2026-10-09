using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// The raw, unvalidated date-range arguments a report command received.
/// </summary>
/// <param name="Date">A single day (<c>yyyy-MM-dd</c>), or <see langword="null"/> when not given.</param>
/// <param name="From">The first day of a range (<c>yyyy-MM-dd</c>), or <see langword="null"/> when not given.</param>
/// <param name="To">The last day of a range (<c>yyyy-MM-dd</c>), or <see langword="null"/> when not given.</param>
/// <param name="Last">The number of days ending today, as typed, or <see langword="null"/> when not given.</param>
/// <param name="Month">A calendar month (<c>yyyy-MM</c>), or <see langword="null"/> when not given.</param>
internal sealed record DateRangeRequest(string? Date = null, string? From = null, string? To = null, int? Last = null, string? Month = null)
{
    /// <summary>
    /// Gets a value indicating whether any date argument was given.
    /// </summary>
    public bool HasAny => Date is not null || From is not null || To is not null || Last is not null || Month is not null;
}

/// <summary>
/// Defines the date-range options shared by the report commands and turns them into a concrete date range.
/// </summary>
internal sealed class DateRangeOptions
{
    /// <summary>
    /// The <c>--date</c> option, or <see langword="null"/> when the command does not offer it.
    /// </summary>
    private readonly Option<string?>? _date;

    /// <summary>
    /// The <c>--from</c> option, or <see langword="null"/> when the command does not offer it.
    /// </summary>
    private readonly Option<string?>? _from;

    /// <summary>
    /// The <c>--to</c> option, or <see langword="null"/> when the command does not offer it.
    /// </summary>
    private readonly Option<string?>? _to;

    /// <summary>
    /// The <c>--last</c> option, or <see langword="null"/> when the command does not offer it.
    /// </summary>
    private readonly Option<int?>? _last;

    /// <summary>
    /// The <c>--month</c> option, or <see langword="null"/> when the command does not offer it.
    /// </summary>
    private readonly Option<string?>? _month;

    /// <summary>
    /// Creates the options and adds them to <paramref name="command"/>.
    /// </summary>
    /// <param name="command">The command that receives the options.</param>
    /// <param name="singleDay">Whether to offer <c>--date</c> (a single day) instead of a range.</param>
    /// <param name="month">Whether to also offer <c>--month</c> (a calendar month).</param>
    public DateRangeOptions(Command command, bool singleDay, bool month = false)
    {
        if (singleDay)
        {
            _date = new Option<string?>("--date") { Description = "Day to show, as yyyy-MM-dd, years 2000-2999 (default: today)" };
            command.Options.Add(_date);
            return;
        }

        _from = new Option<string?>("--from") { Description = "First day of the range, as yyyy-MM-dd, years 2000-2999 (the range ends today unless --to is given; at most 3650 days)" };
        _to = new Option<string?>("--to") { Description = "Last day of the range, as yyyy-MM-dd, years 2000-2999 (requires --from)" };
        _last = new Option<int?>("--last") { Description = "Show the last N days, ending today (1-3650)" };
        command.Options.Add(_from);
        command.Options.Add(_to);
        command.Options.Add(_last);

        if (month)
        {
            _month = new Option<string?>("--month") { Description = "Calendar month to show, as yyyy-MM, years 2000-2999" };
            command.Options.Add(_month);
        }
    }

    /// <summary>
    /// Reads the option values the user supplied.
    /// </summary>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <returns>The raw date-range request.</returns>
    public DateRangeRequest Read(ParseResult parseResult)
    {
        return new DateRangeRequest(
            _date is null ? null : parseResult.GetValue(_date),
            _from is null ? null : parseResult.GetValue(_from),
            _to is null ? null : parseResult.GetValue(_to),
            _last is null ? null : parseResult.GetValue(_last),
            _month is null ? null : parseResult.GetValue(_month));
    }
}
