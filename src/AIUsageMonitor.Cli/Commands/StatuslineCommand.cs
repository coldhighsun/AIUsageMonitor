using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Providers.Claude;
using System.CommandLine;
using System.Globalization;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "statusline" command, meant to be run as Claude Code's status line command: it reads the JSON Claude Code
/// gives it on standard input and keeps the 5-hour and weekly quotas in it (exact, with reset times) for <c>watch</c> to use.
/// It prints nothing unless asked to, and never fails, so it cannot break the status line.
/// </summary>
public static class StatuslineCommand
{
    /// <summary>
    /// Creates the "statusline" command.
    /// </summary>
    /// <param name="store">Where the quotas are kept.</param>
    /// <returns>A configured <see cref="Command"/> instance.</returns>
    public static Command Create(StatuslineLimitsStore store)
    {
        var command = new Command(
            "statusline",
            "Record the usage limits Claude Code gives its status line command (reads its JSON from stdin)");
        var printOption = new Option<bool>("--print")
        {
            Description = "Also print a short status line text, e.g. '5h 23% · 7d 41%'",
        };
        command.Options.Add(printOption);

        command.SetAction(parseResult =>
        {
            string input;
            try
            {
                input = Console.In.ReadToEnd();
            }
            catch (IOException)
            {
                return 0;
            }

            var limits = store.Record(input, DateTimeOffset.UtcNow);
            if (parseResult.GetValue(printOption) && limits is not null)
            {
                Console.WriteLine(FormatLine(limits));
            }

            return 0;
        });
        return command;
    }

    /// <summary>
    /// Formats the quotas as one short line for a status line.
    /// </summary>
    /// <param name="limits">The quotas.</param>
    /// <returns>For example <c>5h 23% · 7d 41%</c>; empty when there are none.</returns>
    internal static string FormatLine(OnlineLimits limits)
    {
        var parts = new List<string>();
        if (limits.Session is { } session)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"5h {session.Percent:0.#}%"));
        }

        if (limits.Week is { } week)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"7d {week.Percent:0.#}%"));
        }

        return string.Join(" · ", parts);
    }
}
