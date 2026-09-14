using Spectre.Console;
using System.CommandLine;
using System.Globalization;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Shared parsing/persistence/interactive-prompt logic for the <c>--session-anchor</c>/<c>--week-anchor</c>
/// options used by <c>watch --view limits</c> to pin the "Current Session" (rolling 5-hour window)
/// and "This Week" usage panels to the account's real reset times. The real reset anchors live on
/// the Anthropic account and cannot be read locally, so they are either supplied by the user or
/// estimated locally from transcript timestamps.
/// </summary>
internal static class LimitsAnchors
{
    private const string SessionAnchorFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// Creates the shared <c>--session-anchor</c>/<c>--week-anchor</c> options.
    /// </summary>
    internal static (Option<string?> SessionAnchor, Option<string?> WeekAnchor) CreateOptions()
    {
        var sessionAnchorOption = new Option<string?>("--session-anchor")
        {
            Description = $"Real current-session window start time in local time ({SessionAnchorFormat}, e.g. 2026-09-14 19:50). Only used when --view limits."
        };
        var weekAnchorOption = new Option<string?>("--week-anchor")
        {
            Description = "Real weekly reset anchor as 'Ddd HH:mm' (e.g. 'Mon 09:00'). Only used when --view limits."
        };
        return (sessionAnchorOption, weekAnchorOption);
    }

    /// <summary>
    /// Parses a weekly reset anchor string of the form <c>"Ddd HH:mm"</c> (e.g. <c>"Mon 09:00"</c>)
    /// into a day-of-week and local time-of-day pair.
    /// </summary>
    internal static bool TryParseWeekAnchor(string value, out (DayOfWeek Day, TimeSpan TimeOfDay) result)
    {
        result = default;
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!TryParseDayOfWeek(parts[0], out var day))
        {
            return false;
        }

        if (!TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var timeOfDay))
        {
            return false;
        }

        result = (day, timeOfDay);
        return true;
    }

    /// <summary>
    /// Resolves the effective current-session and weekly anchors from command-line arguments,
    /// previously persisted settings, and (when running in an interactive terminal) an interactive
    /// prompt for whichever anchor is still missing. Any anchor obtained this way — from an
    /// explicit argument or from the interactive prompt — is persisted back to
    /// <see cref="LimitsSettingsStore"/> so future runs no longer need to ask.
    /// </summary>
    /// <param name="sessionAnchorArg">The raw <c>--session-anchor</c> argument, if provided.</param>
    /// <param name="weekAnchorArg">The raw <c>--week-anchor</c> argument, if provided.</param>
    /// <param name="sessionAnchor">The resolved session anchor, or <see langword="null"/> to fall back to a local estimate.</param>
    /// <param name="weekAnchor">The resolved weekly anchor, or <see langword="null"/> to fall back to a local estimate.</param>
    /// <param name="error">An error message describing why resolution failed, if it did.</param>
    /// <returns><see langword="true"/> if resolution succeeded (even if one or both anchors are unset); <see langword="false"/> if an explicit argument was invalid.</returns>
    internal static bool TryResolve(
        string? sessionAnchorArg, string? weekAnchorArg,
        out DateTimeOffset? sessionAnchor, out (DayOfWeek Day, TimeSpan TimeOfDay)? weekAnchor,
        out string? error)
    {
        error = null;

        if (sessionAnchorArg is not null)
        {
            if (!TryParseSessionAnchor(sessionAnchorArg, out var parsed))
            {
                sessionAnchor = null;
                weekAnchor = null;
                error = $"Invalid --session-anchor '{sessionAnchorArg}'. Expected local time as '{SessionAnchorFormat}', e.g. 2026-09-14 19:50.";
                return false;
            }
            sessionAnchor = parsed;
        }
        else
        {
            sessionAnchor = null;
        }

        if (weekAnchorArg is not null)
        {
            if (!TryParseWeekAnchor(weekAnchorArg, out var parsed))
            {
                sessionAnchor = null;
                weekAnchor = null;
                error = $"Invalid --week-anchor '{weekAnchorArg}'. Expected 'Ddd HH:mm', e.g. 'Mon 09:00'.";
                return false;
            }
            weekAnchor = parsed;
        }
        else
        {
            weekAnchor = null;
        }

        var saved = LimitsSettingsStore.Load();
        var explicitlyProvided = sessionAnchorArg is not null || weekAnchorArg is not null;

        var effectiveSessionAnchor = sessionAnchorArg is not null ? sessionAnchor : saved.SessionAnchor;
        var effectiveWeekAnchorRaw = weekAnchorArg ?? saved.WeekAnchor;
        var effectiveWeekAnchor = weekAnchorArg is not null
            ? weekAnchor
            : (saved.WeekAnchor is not null && TryParseWeekAnchor(saved.WeekAnchor, out var savedWeekAnchor) ? savedWeekAnchor : null);

        var promptedForAnything = false;

        if (effectiveSessionAnchor is null && !Console.IsInputRedirected)
        {
            effectiveSessionAnchor = PromptForSessionAnchor();
            promptedForAnything = true;
        }

        if (effectiveWeekAnchor is null && !Console.IsInputRedirected)
        {
            (effectiveWeekAnchor, effectiveWeekAnchorRaw) = PromptForWeekAnchor();
            promptedForAnything = true;
        }

        if (explicitlyProvided || promptedForAnything)
        {
            LimitsSettingsStore.Save(new(effectiveSessionAnchor, effectiveWeekAnchorRaw));
        }

        sessionAnchor = effectiveSessionAnchor;
        weekAnchor = effectiveWeekAnchor;
        return true;
    }

    private static DateTimeOffset? PromptForSessionAnchor()
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>(
                $"[yellow]No Current Session anchor configured.[/] Enter the real session start time " +
                $"(local time, format '{SessionAnchorFormat}', e.g. 2026-09-14 19:50), or press Enter to use a local estimate:", "");

            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            if (TryParseSessionAnchor(input, out var parsed))
            {
                return parsed;
            }

            AnsiConsole.MarkupLine($"[red]Invalid format. Expected '{SessionAnchorFormat}', e.g. 2026-09-14 19:50.[/]");
        }
    }

    private static ((DayOfWeek Day, TimeSpan TimeOfDay)? Anchor, string? Raw) PromptForWeekAnchor()
    {
        while (true)
        {
            var input = AnsiConsole.Ask<string>(
                "[yellow]No This Week anchor configured.[/] Enter the real weekly reset time " +
                "(format 'Ddd HH:mm', e.g. 'Mon 09:00'), or press Enter to use a local estimate:", "");

            if (string.IsNullOrWhiteSpace(input))
            {
                return (null, null);
            }

            if (TryParseWeekAnchor(input, out var parsed))
            {
                return (parsed, input);
            }

            AnsiConsole.MarkupLine("[red]Invalid format. Expected 'Ddd HH:mm', e.g. 'Mon 09:00'.[/]");
        }
    }

    private static bool TryParseDayOfWeek(string value, out DayOfWeek day)
    {
        foreach (var candidate in Enum.GetValues<DayOfWeek>())
        {
            if (candidate.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                day = candidate;
                return true;
            }
        }

        day = default;
        return false;
    }

    private static bool TryParseSessionAnchor(string value, out DateTimeOffset result)
    {
        if (!DateTime.TryParseExact(value, SessionAnchorFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDateTime))
        {
            result = default;
            return false;
        }

        result = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
        return true;
    }
}