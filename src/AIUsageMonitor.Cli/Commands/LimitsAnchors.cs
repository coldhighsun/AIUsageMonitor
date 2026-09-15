using Spectre.Console;
using System.CommandLine;
using System.Globalization;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Shared parsing/persistence/interactive-prompt logic for the <c>--session-reset</c>/<c>--week-reset</c>
/// options used by <c>watch --view limits</c> to pin the "Current Session" (rolling 5-hour window)
/// and "This Week" usage panels to the account's real reset times. Those live on the Anthropic
/// account and cannot be read locally, so they are either supplied by the user - copied from the
/// reset time Claude itself displays - or estimated locally from transcript timestamps.
/// </summary>
internal static class LimitsAnchors
{
    private const string SessionResetFormat = "HH:mm";
    private static readonly TimeSpan SessionWindowDuration = TimeSpan.FromHours(5);

    /// <summary>
    /// Creates the shared <c>--session-reset</c>/<c>--week-reset</c> options.
    /// </summary>
    internal static (Option<string?> SessionReset, Option<string?> WeekReset) CreateOptions()
    {
        var sessionResetOption = new Option<string?>("--session-reset")
        {
            Description = $"Real reset time of the current session window as local '{SessionResetFormat}' (e.g. 18:30), as shown by Claude itself. Only used when --view limits."
        };
        var weekResetOption = new Option<string?>("--week-reset")
        {
            Description = "Real weekly reset time as 'Ddd HH:mm' (e.g. 'Mon 09:00'), as shown by Claude itself. This is a fixed weekly schedule for your account, so it only needs to be set once. Only used when --view limits."
        };
        return (sessionResetOption, weekResetOption);
    }

    /// <summary>
    /// Parses a weekly reset time string of the form <c>"Ddd HH:mm"</c> (e.g. <c>"Mon 09:00"</c>)
    /// into a day-of-week and local time-of-day pair.
    /// </summary>
    internal static bool TryParseWeekReset(string value, out (DayOfWeek Day, TimeSpan TimeOfDay) result)
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
    /// Resolves the effective session and weekly reset times from command-line arguments,
    /// previously persisted settings, and (when running in an interactive terminal) an interactive
    /// prompt for whichever one is still missing. Anything obtained this way — from an explicit
    /// argument or from the interactive prompt — is persisted back to
    /// <see cref="LimitsSettingsStore"/> so future runs no longer need to ask.
    /// </summary>
    /// <param name="sessionResetArg">The raw <c>--session-reset</c> argument, if provided.</param>
    /// <param name="weekResetArg">The raw <c>--week-reset</c> argument, if provided.</param>
    /// <param name="sessionResetAt">The resolved session reset time, or <see langword="null"/> to fall back to a local estimate.</param>
    /// <param name="weekResetAt">The resolved weekly reset time, or <see langword="null"/> to fall back to a local estimate.</param>
    /// <param name="error">An error message describing why resolution failed, if it did.</param>
    /// <returns><see langword="true"/> if resolution succeeded (even if one or both are unset); <see langword="false"/> if an explicit argument was invalid.</returns>
    internal static bool TryResolve(
        string? sessionResetArg, string? weekResetArg,
        out DateTimeOffset? sessionResetAt, out (DayOfWeek Day, TimeSpan TimeOfDay)? weekResetAt,
        out string? error)
    {
        error = null;

        if (sessionResetArg is not null)
        {
            if (!TryParseSessionReset(sessionResetArg, out var parsed, out var parseError))
            {
                sessionResetAt = null;
                weekResetAt = null;
                error = $"Invalid --session-reset '{sessionResetArg}'. {parseError}";
                return false;
            }
            sessionResetAt = parsed;
        }
        else
        {
            sessionResetAt = null;
        }

        if (weekResetArg is not null)
        {
            if (!TryParseWeekReset(weekResetArg, out var parsed))
            {
                sessionResetAt = null;
                weekResetAt = null;
                error = $"Invalid --week-reset '{weekResetArg}'. Expected 'Ddd HH:mm', e.g. 'Mon 09:00'.";
                return false;
            }
            weekResetAt = parsed;
        }
        else
        {
            weekResetAt = null;
        }

        var saved = LimitsSettingsStore.Load();
        var explicitlyProvided = sessionResetArg is not null || weekResetArg is not null;

        var effectiveSessionReset = sessionResetArg is not null ? sessionResetAt : saved.SessionResetAt;
        var effectiveWeekResetRaw = weekResetArg ?? saved.WeekResetAt;
        var effectiveWeekReset = weekResetArg is not null
            ? weekResetAt
            : (saved.WeekResetAt is not null && TryParseWeekReset(saved.WeekResetAt, out var savedWeekReset) ? savedWeekReset : null);

        var promptedForAnything = false;

        // A persisted reset time that has already elapsed is deliberately not prompted for again:
        // it simply stops applying, the window falls back to an estimate or to "unknown", and the
        // limits view tells the user how to recalibrate without blocking startup.
        if (effectiveSessionReset is null && !Console.IsInputRedirected)
        {
            effectiveSessionReset = PromptForSessionReset(currentValue: null);
            promptedForAnything = true;
        }

        if (effectiveWeekReset is null && !Console.IsInputRedirected)
        {
            (effectiveWeekReset, effectiveWeekResetRaw) = PromptForWeekReset(currentRaw: null);
            promptedForAnything = true;
        }

        if (explicitlyProvided || promptedForAnything)
        {
            LimitsSettingsStore.Save(new(effectiveSessionReset, effectiveWeekResetRaw));
        }

        sessionResetAt = effectiveSessionReset;
        weekResetAt = effectiveWeekReset;
        return true;
    }

    /// <summary>
    /// Interactively prompts for both the session and weekly reset times and persists the result.
    /// Used by <c>watch</c>'s recalibration hotkey. Each prompt shows the currently configured
    /// value (if any) as its default, so pressing Enter keeps it unchanged — the same value is
    /// then returned as-is, without being re-validated against the 5-hour session window, so a
    /// stale (already elapsed) session reset can still be kept without an error round-trip.
    /// </summary>
    /// <returns>The resolved session and weekly reset times.</returns>
    internal static (DateTimeOffset? SessionResetAt, (DayOfWeek Day, TimeSpan TimeOfDay)? WeekResetAt) PromptAndSaveBoth()
    {
        var saved = LimitsSettingsStore.Load();

        var sessionResetAt = PromptForSessionReset(saved.SessionResetAt);
        var (weekResetAt, weekResetRaw) = PromptForWeekReset(saved.WeekResetAt);

        LimitsSettingsStore.Save(new(sessionResetAt, weekResetRaw));
        return (sessionResetAt, weekResetAt);
    }

    /// <param name="currentValue">
    /// The currently configured reset time, if any. Shown as the prompt's default so pressing
    /// Enter keeps it. A fresh entry (including retyping the same clock time, e.g. to confirm it
    /// still applies) is always parsed and validated normally; the stale <paramref name="currentValue"/>
    /// is only reused verbatim as a last resort, when that parse fails because the accepted default
    /// itself has fallen outside the 5-hour window - so it can still be kept without an error.
    /// </param>
    private static DateTimeOffset? PromptForSessionReset(DateTimeOffset? currentValue)
    {
        var defaultText = currentValue is { } v ? v.ToString(SessionResetFormat) : "";
        var promptText = currentValue is not null
            ? "[yellow]Current Session reset time.[/] Enter the reset time Claude shows " +
              $"(local '{SessionResetFormat}', e.g. 18:30), or press Enter to keep the current value:"
            : "[yellow]Current Session reset time not set.[/] Enter the reset time Claude shows " +
              $"(local '{SessionResetFormat}', e.g. 18:30), or press Enter to use a local estimate:";

        while (true)
        {
            var input = AnsiConsole.Ask<string>(promptText, defaultText);

            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            if (TryParseSessionReset(input, out var parsed, out var parseError))
            {
                return parsed;
            }

            if (input == defaultText && currentValue is not null)
            {
                return currentValue;
            }

            AnsiConsole.MarkupLine($"[red]{parseError}[/]");
        }
    }

    /// <param name="currentRaw">
    /// The currently configured reset time, in its originally typed 'Ddd HH:mm' form, if any. Shown
    /// as the prompt's default so pressing Enter keeps it.
    /// </param>
    private static ((DayOfWeek Day, TimeSpan TimeOfDay)? Reset, string? Raw) PromptForWeekReset(string? currentRaw)
    {
        var defaultText = currentRaw ?? "";
        var promptText = currentRaw is not null
            ? "[yellow]This Week reset time.[/] This is a fixed weekly schedule for your account " +
              "(check Settings > Usage, or /usage in Claude Code) — enter it as 'Ddd HH:mm' " +
              "(e.g. 'Mon 09:00'), or press Enter to keep the current value:"
            : "[yellow]This Week reset time not set.[/] This is a fixed weekly schedule for your account " +
              "(check Settings > Usage, or /usage in Claude Code), so it only needs to be entered once — " +
              "format 'Ddd HH:mm' (e.g. 'Mon 09:00'), or press Enter to use a local estimate:";

        while (true)
        {
            var input = AnsiConsole.Ask<string>(promptText, defaultText);

            if (string.IsNullOrWhiteSpace(input))
            {
                return (null, null);
            }

            if (TryParseWeekReset(input, out var parsed))
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

    /// <summary>
    /// Parses a local time-of-day such as <c>"18:30"</c> into the next instant it occurs. An active
    /// window's reset necessarily falls within the next 5 hours, so the date is inferred from that
    /// and anything further out is rejected as a typo.
    /// </summary>
    private static bool TryParseSessionReset(string value, out DateTimeOffset result, out string? error)
    {
        result = default;

        if (!TimeOnly.TryParseExact(value.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay))
        {
            error = $"Expected a local time of day as '{SessionResetFormat}', e.g. 18:30.";
            return false;
        }

        var now = DateTimeOffset.Now;
        var localDateTime = now.DateTime.Date + timeOfDay.ToTimeSpan();
        var candidate = ToLocalOffset(localDateTime);

        if (candidate <= now)
        {
            candidate = ToLocalOffset(localDateTime.AddDays(1));
        }

        if (candidate - now > SessionWindowDuration)
        {
            error = $"A session reset can only be up to {SessionWindowDuration.TotalHours:0} hours away, but '{value}' is {(candidate - now).TotalHours:0.#} hours out.";
            return false;
        }

        result = candidate;
        error = null;
        return true;
    }

    private static DateTimeOffset ToLocalOffset(DateTime localDateTime)
        => new(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
}