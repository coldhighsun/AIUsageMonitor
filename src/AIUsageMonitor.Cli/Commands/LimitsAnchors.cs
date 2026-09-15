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
    /// Creates the shared <c>--session-token-progress</c>/<c>--week-token-progress</c> options. These
    /// take a *percentage* (what Claude itself displays), not a token count - see
    /// <see cref="ResolveTokenLimit"/> for how that percentage is turned into a token limit.
    /// </summary>
    internal static (Option<double?> SessionTokenProgress, Option<double?> WeekTokenProgress) CreateTokenProgressOptions()
    {
        var sessionTokenProgressOption = new Option<double?>("--session-token-progress")
        {
            Description = "Current session usage percentage as shown on Claude's own usage page (Settings > Usage, or /usage in Claude Code), e.g. 32 for '32%'. Combined with the tokens counted so far this session to derive a token budget, which is then used to track the token-progress bar live. Only used when --view limits."
        };
        var weekTokenProgressOption = new Option<double?>("--week-token-progress")
        {
            Description = "Current weekly usage percentage as shown on Claude's own usage page, e.g. 32 for '32%'. Combined with the tokens counted so far this week to derive a token budget. Only used when --view limits."
        };
        return (sessionTokenProgressOption, weekTokenProgressOption);
    }

    /// <summary>
    /// Interactively prompts for both the session and weekly reset times and token limits, and
    /// persists the result. Used by <c>watch</c>'s recalibration hotkey. Each prompt shows the
    /// currently configured value (if any) as its default, so pressing Enter keeps it unchanged - the
    /// same value is then returned as-is, without being re-validated against the 5-hour session
    /// window, so a stale (already elapsed) session reset can still be kept without an error round-trip.
    /// </summary>
    /// <param name="sessionTokensSoFar">The tokens counted for the current session window so far, used to re-derive its token limit from a freshly entered percentage.</param>
    /// <param name="weekTokensSoFar">The tokens counted for the current week window so far.</param>
    /// <returns>The resolved session/weekly reset times and session/weekly token limits.</returns>
    internal static (
        DateTimeOffset? SessionResetAt, (DayOfWeek Day, TimeSpan TimeOfDay)? WeekResetAt,
        long? SessionTokenLimit, long? WeekTokenLimit) PromptAndSaveBoth(long sessionTokensSoFar, long weekTokensSoFar)
    {
        var saved = LimitsSettingsStore.Load();

        var sessionResetAt = PromptForSessionReset(saved.SessionResetAt);
        var (weekResetAt, weekResetRaw) = PromptForWeekReset(saved.WeekResetAt);
        var sessionTokenLimit = PromptForTokenLimit(
            saved.SessionTokenLimit, sessionTokensSoFar,
            "[yellow]Session usage percentage.[/] Enter the percentage Claude's usage display shows "
                + "(Settings > Usage, or /usage in Claude Code), e.g. 32, or press Enter to keep the current value:");
        var weekTokenLimit = PromptForTokenLimit(
            saved.WeekTokenLimit, weekTokensSoFar,
            "[yellow]Weekly usage percentage.[/] Enter the percentage Claude's usage display shows "
                + "(Settings > Usage, or /usage in Claude Code), e.g. 32, or press Enter to keep the current value:");

        LimitsSettingsStore.Save(new(sessionResetAt, weekResetRaw, sessionTokenLimit, weekTokenLimit));
        return (sessionResetAt, weekResetAt, sessionTokenLimit, weekTokenLimit);
    }

    /// <summary>
    /// Resolves the token limit for one usage window (session or week) from a usage *percentage* -
    /// either an explicit CLI argument or (in an interactive terminal) a prompt - combined with the
    /// tokens this tool has counted for that window so far: <c>limit = tokensSoFar / (percent / 100)</c>.
    /// Claude itself never shows the underlying limit, only a percentage, so this is the only way to
    /// derive one locally. The derived limit (not the percentage) is what gets persisted and returned,
    /// so future refreshes can keep tracking the token-progress bar live against it without asking
    /// again - until the user recalibrates (e.g. because the account's limit changed).
    /// </summary>
    /// <param name="progressPercentArg">The raw <c>--session-token-progress</c>/<c>--week-token-progress</c> argument, if provided.</param>
    /// <param name="savedLimit">The previously persisted token limit for this window, if any.</param>
    /// <param name="tokensSoFar">The tokens this tool has counted for the window so far.</param>
    /// <param name="promptLabel">A short label (e.g. "Session", "Weekly") used in the interactive prompt text.</param>
    /// <param name="tokenLimit">The resolved token limit, or <see langword="null"/> if not configured.</param>
    /// <param name="error">An error message describing why resolution failed, if it did.</param>
    /// <returns><see langword="true"/> if resolution succeeded; <see langword="false"/> if an explicit argument was invalid.</returns>
    internal static bool ResolveTokenLimit(
        double? progressPercentArg, long? savedLimit, long tokensSoFar, string promptLabel,
        out long? tokenLimit, out string? error)
    {
        error = null;

        if (progressPercentArg is not null)
        {
            if (progressPercentArg <= 0)
            {
                tokenLimit = null;
                error = $"Invalid usage percentage '{progressPercentArg}'. Expected a percentage greater than 0.";
                return false;
            }

            tokenLimit = DeriveTokenLimit(tokensSoFar, progressPercentArg.Value);
            return true;
        }

        if (savedLimit is not null || Console.IsInputRedirected)
        {
            tokenLimit = savedLimit;
            return true;
        }

        tokenLimit = PromptForTokenLimit(
            currentLimit: null, tokensSoFar,
            $"[yellow]{promptLabel} usage percentage not set.[/] Enter the percentage Claude's usage display shows "
                + "(Settings > Usage, or /usage in Claude Code), e.g. 32, or press Enter to skip the token progress bar:");
        return true;
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
            // Token limits are resolved separately (see ResolveTokenLimit), since deriving them needs
            // the tokens counted for the window - which isn't known until after the reset times below
            // are settled. Carry the currently saved values through unchanged.
            LimitsSettingsStore.Save(saved with
            {
                SessionResetAt = effectiveSessionReset,
                WeekResetAt = effectiveWeekResetRaw
            });
        }

        sessionResetAt = effectiveSessionReset;
        weekResetAt = effectiveWeekReset;
        return true;
    }

    /// <summary>
    /// Derives a token limit from the tokens counted so far and the percentage of that limit they
    /// represent, as read off Claude's own usage display. Never returns less than 1, since a 0 limit
    /// would later be divided into and would also get persisted as <c>savedLimit</c>, silently
    /// breaking the token-progress bar until the user recalibrates - most commonly when tokensSoFar
    /// is still 0 right after the window has reset.
    /// </summary>
    private static long DeriveTokenLimit(long tokensSoFar, double progressPercent)
        => Math.Max(1, (long)Math.Round(tokensSoFar / (progressPercent / 100)));

    /// <summary>
    /// Prompts for a string with <paramref name="defaultText"/> as the value Enter returns (so a
    /// blank <paramref name="defaultText"/> still lets the user skip by pressing Enter), but only
    /// displays that default when it's non-empty - <see cref="AnsiConsole.Ask{T}(string, T)"/> would
    /// otherwise render an empty pair of parentheses (e.g. <c>"Enter a value ():"</c>) when there's
    /// no current value to default to.
    /// </summary>
    private static string Ask(string promptText, string defaultText)
        => new TextPrompt<string>(promptText)
            .DefaultValue(defaultText)
            .ShowDefaultValue(!string.IsNullOrEmpty(defaultText))
            .AllowEmpty()
            .Show(AnsiConsole.Console);

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
            var input = Ask(promptText, defaultText);

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

    /// <summary>
    /// Prompts for a usage percentage and derives a token limit from it and <paramref name="tokensSoFar"/>.
    /// The prompt's default is the percentage <paramref name="currentLimit"/> currently implies (given
    /// <paramref name="tokensSoFar"/>), so pressing Enter keeps the existing limit unchanged rather
    /// than re-deriving it from a rounded default percentage.
    /// </summary>
    /// <param name="currentLimit">The currently configured token limit, if any.</param>
    /// <param name="tokensSoFar">The tokens counted for the window so far.</param>
    /// <param name="promptText">The prompt text to display.</param>
    private static long? PromptForTokenLimit(long? currentLimit, long tokensSoFar, string promptText)
    {
        var defaultText = currentLimit is { } limit and > 0
            ? (tokensSoFar * 100.0 / limit).ToString("0.##", CultureInfo.InvariantCulture)
            : "";

        while (true)
        {
            var input = Ask(promptText, defaultText);

            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            if (input == defaultText && currentLimit is not null)
            {
                return currentLimit;
            }

            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return DeriveTokenLimit(tokensSoFar, parsed);
            }

            AnsiConsole.MarkupLine("[red]Invalid value. Expected a percentage greater than 0, e.g. 32.[/]");
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
            var input = Ask(promptText, defaultText);

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

    private static DateTimeOffset ToLocalOffset(DateTime localDateTime)
        => new(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));

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
}