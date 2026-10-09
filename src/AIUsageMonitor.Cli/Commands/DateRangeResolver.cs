using System.Globalization;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// An inclusive range of days.
/// </summary>
/// <param name="From">The first day.</param>
/// <param name="To">The last day.</param>
internal readonly record struct DateRange(DateOnly From, DateOnly To);

/// <summary>
/// The outcome of resolving a <see cref="DateRangeRequest"/>: either a range or a user-facing error.
/// Only <see cref="Ok"/> and <see cref="Fail"/> create one, so a result never carries both.
/// </summary>
internal readonly struct DateRangeResolution
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DateRangeResolution"/> struct.
    /// </summary>
    /// <param name="range">The resolved range, or <see langword="null"/> when the request was invalid.</param>
    /// <param name="error">A user-facing message when the request was invalid.</param>
    private DateRangeResolution(DateRange? range, string? error)
    {
        Range = range;
        Error = error;
    }

    /// <summary>
    /// Gets the resolved range, or <see langword="null"/> when the request was invalid.
    /// </summary>
    public DateRange? Range { get; }

    /// <summary>
    /// Gets a user-facing message when the request was invalid.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// Creates a successful resolution.
    /// </summary>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day.</param>
    /// <returns>A resolution carrying the range.</returns>
    public static DateRangeResolution Ok(DateOnly from, DateOnly to)
    {
        return new DateRangeResolution(new DateRange(from, to), null);
    }

    /// <summary>
    /// Creates a failed resolution.
    /// </summary>
    /// <param name="error">The user-facing message.</param>
    /// <returns>A resolution carrying the error.</returns>
    public static DateRangeResolution Fail(string error)
    {
        return new DateRangeResolution(null, error);
    }
}

/// <summary>
/// Turns the raw date-range arguments of a report command into a concrete, validated date range.
/// </summary>
internal static class DateRangeResolver
{
    /// <summary>
    /// The most days a range may span, whether given as <c>--last</c> or as <c>--from</c>/<c>--to</c>.
    /// </summary>
    public const int MaxDays = 3650;

    /// <summary>
    /// The earliest year any date option accepts.
    /// </summary>
    private const int MinYear = 2000;

    /// <summary>
    /// The latest year any date option accepts; keeps day-by-day iteration clear of <see cref="DateOnly.MaxValue"/>.
    /// </summary>
    private const int MaxYear = 2999;

    /// <summary>
    /// Gets the current local date.
    /// </summary>
    /// <param name="clock">The clock to read.</param>
    /// <returns>Today's date in the clock's local time zone.</returns>
    public static DateOnly Today(TimeProvider clock)
    {
        return DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    }

    /// <summary>
    /// Resolves a request into an inclusive date range.
    /// </summary>
    /// <param name="request">The raw arguments.</param>
    /// <param name="today">The current local date.</param>
    /// <param name="defaultRange">The length of the range used when no argument was given, ending today.</param>
    /// <returns>The range, or a user-facing error.</returns>
    public static DateRangeResolution Resolve(DateRangeRequest request, DateOnly today, TimeSpan defaultRange)
    {
        var groups = (request.Date is not null ? 1 : 0)
            + (request.From is not null || request.To is not null ? 1 : 0)
            + (request.Last is not null ? 1 : 0)
            + (request.Month is not null ? 1 : 0);
        if (groups > 1)
        {
            return DateRangeResolution.Fail("Specify only one of --date, --from/--to, --last or --month.");
        }

        if (request.Date is not null)
        {
            return ResolveDate(request.Date);
        }

        if (request.From is not null || request.To is not null)
        {
            return ResolveFromTo(request.From, request.To, today);
        }

        if (request.Last is not null)
        {
            return ResolveLast(request.Last.Value, today);
        }

        if (request.Month is not null)
        {
            return ResolveMonth(request.Month);
        }

        return DateRangeResolution.Ok(StartOfLast(today, (int)defaultRange.TotalDays), today);
    }

    /// <summary>
    /// Resolves a single <c>--date</c> day.
    /// </summary>
    /// <param name="date">The text given to <c>--date</c>.</param>
    /// <returns>A one-day range, or an error.</returns>
    private static DateRangeResolution ResolveDate(string date)
    {
        var error = TryParseDay(date, "--date", out var day);
        return error is null ? DateRangeResolution.Ok(day, day) : DateRangeResolution.Fail(error);
    }

    /// <summary>
    /// Resolves a <c>--from</c> / <c>--to</c> pair.
    /// </summary>
    /// <param name="fromText">The text given to <c>--from</c>, if any.</param>
    /// <param name="toText">The text given to <c>--to</c>, if any.</param>
    /// <param name="today">The current local date, used when <c>--to</c> is omitted.</param>
    /// <returns>The range, or an error.</returns>
    private static DateRangeResolution ResolveFromTo(string? fromText, string? toText, DateOnly today)
    {
        if (fromText is null)
        {
            return DateRangeResolution.Fail("--to requires --from.");
        }

        if (TryParseDay(fromText, "--from", out var from) is { } fromError)
        {
            return DateRangeResolution.Fail(fromError);
        }

        var to = today;
        if (toText is not null && TryParseDay(toText, "--to", out to) is { } toError)
        {
            return DateRangeResolution.Fail(toError);
        }

        if (from > to)
        {
            return DateRangeResolution.Fail($"--from ({from:yyyy-MM-dd}) must not be after the end of the range ({to:yyyy-MM-dd}).");
        }

        var days = to.DayNumber - from.DayNumber + 1;
        return days > MaxDays
            ? DateRangeResolution.Fail($"The range must not be longer than {MaxDays} days (got {days}).")
            : DateRangeResolution.Ok(from, to);
    }

    /// <summary>
    /// Resolves <c>--last</c>.
    /// </summary>
    /// <param name="last">The requested number of days, as typed.</param>
    /// <param name="today">The current local date.</param>
    /// <returns>The range ending today, or an error.</returns>
    private static DateRangeResolution ResolveLast(int last, DateOnly today)
    {
        if (last < 1)
        {
            return DateRangeResolution.Fail($"--last must be at least 1 (got {last}).");
        }

        if (last > MaxDays)
        {
            return DateRangeResolution.Fail($"--last must be at most {MaxDays} (got {last}).");
        }

        return DateRangeResolution.Ok(StartOfLast(today, last), today);
    }

    /// <summary>
    /// Resolves <c>--month</c>.
    /// </summary>
    /// <param name="month">The text given to <c>--month</c>.</param>
    /// <returns>The whole calendar month, or an error.</returns>
    private static DateRangeResolution ResolveMonth(string month)
    {
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            return DateRangeResolution.Fail($"Invalid --month '{month}'; expected yyyy-MM.");
        }

        if (first.Year is < MinYear or > MaxYear)
        {
            return DateRangeResolution.Fail($"--month year must be between {MinYear} and {MaxYear}.");
        }

        return DateRangeResolution.Ok(first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>
    /// Gets the first day of a range of the given length that ends today.
    /// </summary>
    /// <param name="today">The last day of the range.</param>
    /// <param name="days">The length of the range, in days.</param>
    /// <returns>The first day of the range.</returns>
    private static DateOnly StartOfLast(DateOnly today, int days)
    {
        return today.AddDays(1 - days);
    }

    /// <summary>
    /// Parses a <c>yyyy-MM-dd</c> day and checks that its year is in the supported range.
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="optionName">The option the text came from, used in the error message.</param>
    /// <param name="day">The parsed day.</param>
    /// <returns>A user-facing message when the text is not a supported day; <see langword="null"/> otherwise.</returns>
    private static string? TryParseDay(string text, string optionName, out DateOnly day)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
        {
            return $"Invalid {optionName} '{text}'; expected yyyy-MM-dd.";
        }

        return day.Year is < MinYear or > MaxYear
            ? $"{optionName} year must be between {MinYear} and {MaxYear}."
            : null;
    }
}
