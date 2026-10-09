using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// What a list command fixes once, when it is created, and passes to every run of <see cref="UsageList"/>.
/// </summary>
/// <param name="Clock">The clock that decides what "today" is.</param>
/// <param name="DefaultRange">The length of the range used when no range option is given, ending today.</param>
/// <param name="RangeOptions">The date-range options of the command.</param>
/// <param name="ListOptions">The list options of the command.</param>
/// <param name="Noun">What the empty-result hint says was not found, e.g. <c>sessions</c>.</param>
internal sealed record UsageListSetup(
    TimeProvider Clock, TimeSpan DefaultRange, DateRangeOptions RangeOptions, ListOptions ListOptions, string Noun);

/// <summary>
/// The flow shared by the project and session lists: resolve the date range, read the list options, load, order, and print
/// JSON, a hint when nothing matched, or the table.
/// </summary>
internal static class UsageList
{
    /// <summary>
    /// Runs a list command.
    /// </summary>
    /// <typeparam name="TItem">The type of the rows being listed.</typeparam>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <param name="setup">What the command fixed when it was created.</param>
    /// <param name="json">Whether to print JSON.</param>
    /// <param name="load">Loads every matching row for a date range and the list options, reporting progress.</param>
    /// <param name="sort">Orders the rows and keeps the first few, as the list options ask.</param>
    /// <param name="writeJson">Prints the JSON report for the range, the number of matching rows and the rows shown.</param>
    /// <param name="writeTable">Prints the table for the range, the number of matching rows, the rows shown and the list options.</param>
    /// <returns>The exit code: 0 on success, 1 when the date range is invalid.</returns>
    public static int Run<TItem>(
        ParseResult parseResult,
        UsageListSetup setup,
        bool json,
        Func<DateRange, ListRequest, IProgress<int>?, List<TItem>> load,
        Func<List<TItem>, ListRequest, List<TItem>> sort,
        Action<DateRange, int, List<TItem>> writeJson,
        Action<DateRange, int, List<TItem>, ListRequest> writeTable)
    {
        var resolution = DateRangeResolver.Resolve(setup.RangeOptions.Read(parseResult), DateRangeResolver.Today(setup.Clock), setup.DefaultRange);
        if (resolution.Range is not { } range)
        {
            Console.Error.WriteLine(resolution.Error);
            return 1;
        }

        var request = setup.ListOptions.Read(parseResult);
        var items = JsonOutput.Load(json, "Loading usage data...", progress => load(range, request, progress));
        var shown = sort(items, request);

        if (json)
        {
            writeJson(range, items.Count, shown);
        }
        else if (items.Count == 0)
        {
            Console.Error.WriteLine($"No {setup.Noun} found between {range.From:yyyy-MM-dd} and {range.To:yyyy-MM-dd}.");
        }
        else
        {
            writeTable(range, items.Count, shown, request);
        }

        return 0;
    }
}
