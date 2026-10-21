using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
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

    /// <summary>
    /// The text that tells a user which usage percentages are accepted.
    /// </summary>
    private static readonly string PercentRangeHint = string.Create(
        CultureInfo.InvariantCulture, $"Expected a percentage between {UsageCalibration.MinPercent} and {UsageCalibration.MaxPercent:0}, e.g. 32.");

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
    /// <see cref="ResolveCalibration"/> for how that percentage is turned into a calibration.
    /// </summary>
    internal static (Option<double?> SessionTokenProgress, Option<double?> WeekTokenProgress) CreateTokenProgressOptions()
    {
        var sessionTokenProgressOption = new Option<double?>("--session-token-progress")
        {
            Description = "Current session usage percentage as shown on Claude's own usage page (Settings > Usage, or /usage in Claude Code), e.g. 32 for '32%'. Combined with the quota-weighted cost (see --cache-read-weight) accrued so far this session to derive a usage budget, which is then used to track the usage-progress bar live. Only used when --view limits."
        };
        var weekTokenProgressOption = new Option<double?>("--week-token-progress")
        {
            Description = "Current weekly usage percentage as shown on Claude's own usage page, e.g. 32 for '32%'. Combined with the quota-weighted cost (see --cache-read-weight) accrued so far this week to derive a usage budget. Only used when --view limits."
        };
        return (sessionTokenProgressOption, weekTokenProgressOption);
    }

    /// <summary>
    /// Interactively prompts for both the session and weekly reset times and token limits, and
    /// persists the result. Used by <c>watch</c>'s recalibration hotkey. Each prompt shows the
    /// currently configured value (if any) as its default, so pressing Enter keeps it unchanged - the
    /// same value is then returned as-is. The session reset default is only ever the reset currently
    /// in effect or a persisted one that is still in the future (see <see cref="SelectSessionResetDefault"/>),
    /// so an already elapsed reset is never offered and cannot be re-applied by pressing Enter.
    /// </summary>
    /// <param name="costSoFarFor">
    /// Computes the estimated session and week cost accrued so far for windows pinned to the reset
    /// times just entered. It is called after the reset prompts and before the percentage prompts,
    /// because the limit is derived from <c>cost / percent</c> and the cost of the window that was
    /// on screen before recalibrating (pinned to the old reset times) differs from the cost of the
    /// window the new reset times select.
    /// </param>
    /// <param name="currentSessionResetAt">The still-upcoming session reset currently shown (confirmed or locally estimated), offered as the prompt's default. Pressing Enter accepts it and pins it as a confirmed anchor.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect, used to show the percentage a kept calibration currently implies. The saved weight setting is carried over unchanged.</param>
    /// <param name="notices">Receives the plain-text notices that explain why a typed percentage was not used, for the caller to show in its view, because anything printed here is cleared with the screen right away.</param>
    /// <returns>The resolved session/weekly reset times and session/weekly usage calibrations.</returns>
    internal static (
        DateTimeOffset? SessionResetAt, (DayOfWeek Day, TimeSpan TimeOfDay)? WeekResetAt,
        UsageCalibration? SessionCalibration, UsageCalibration? WeekCalibration) PromptAndSaveBoth(
        Func<DateTimeOffset?, (DayOfWeek Day, TimeSpan TimeOfDay)?, (CostBreakdown Session, CostBreakdown Week)> costSoFarFor,
        DateTimeOffset? currentSessionResetAt,
        decimal cacheReadWeight,
        ICollection<string> notices)
    {
        var saved = LimitsSettingsStore.Load();

        var sessionResetAt = PromptForSessionReset(
            SelectSessionResetDefault(currentSessionResetAt, saved.SessionResetAt, DateTimeOffset.Now));
        var (weekResetAt, weekResetRaw) = PromptForWeekReset(saved.WeekResetAt);

        var (sessionCostSoFar, weekCostSoFar) = costSoFarFor(sessionResetAt, weekResetAt);
        var sessionCalibration = PromptForCalibration(
            UsageCalibration.OrNullIfInvalid(saved.SessionCalibration), sessionCostSoFar, cacheReadWeight, "Session", notices,
            "[yellow]Session usage %[/] (from /usage), e.g. 32, Enter to keep:");
        var weekCalibration = PromptForCalibration(
            UsageCalibration.OrNullIfInvalid(saved.WeekCalibration), weekCostSoFar, cacheReadWeight, "Weekly", notices,
            "[yellow]Weekly usage %[/] (from /usage), e.g. 32, Enter to keep:");

        LimitsSettingsStore.SaveOrWarn(new(sessionResetAt, weekResetRaw, sessionCalibration, weekCalibration, saved.CacheReadWeight));
        return (sessionResetAt, weekResetAt, sessionCalibration, weekCalibration);
    }

    /// <summary>
    /// Picks the session reset time offered as the Enter-default when recalibrating. The reset
    /// currently in effect wins; otherwise the persisted one is used, but only while it is still in
    /// the future. An elapsed persisted reset must not be offered: accepting it would pin the session
    /// to a past boundary and bring the previous window's data back.
    /// </summary>
    /// <param name="currentResetAt">The confirmed, still-upcoming session reset currently in effect, if any.</param>
    /// <param name="savedResetAt">The persisted session reset time, if any.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The reset time to offer as the default, or <see langword="null"/> for none.</returns>
    internal static DateTimeOffset? SelectSessionResetDefault(
        DateTimeOffset? currentResetAt, DateTimeOffset? savedResetAt, DateTimeOffset now)
        => currentResetAt ?? (savedResetAt is { } saved && saved > now ? saved : null);

    /// <summary>
    /// Picks the session reset time currently shown on screen, whether confirmed or estimated, so
    /// it can be offered as the Enter-default when recalibrating. An elapsed or missing reset is not offered.
    /// </summary>
    /// <param name="window">The session window last rendered, if any.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The still-upcoming reset time of the window, or <see langword="null"/> for none.</returns>
    internal static DateTimeOffset? SelectShownSessionReset(UsageWindowSummary? window, DateTimeOffset now)
        => window?.ResetsAt is { } shown && shown > now ? shown : null;

    /// <summary>
    /// Resolves the usage calibration for one usage window (session or week) from a usage
    /// *percentage* - either an explicit CLI argument or (in an interactive terminal) a prompt -
    /// paired with the cost this tool has computed for that window so far. The limit is later derived
    /// from it as <c>limit = quotaCost / (percent / 100)</c> (see <see cref="UsageCalibration"/> and
    /// <see cref="QuotaCost"/>). Cost already weights tokens by model and type
    /// (an Opus token costs far more toward the real limit than a Haiku one), which tracks Anthropic's
    /// actual usage gating much more closely than a flat token count would. Claude itself never shows
    /// the underlying limit, only a percentage, so this is the only way to derive one locally. The
    /// calibration (not the derived limit) is what gets persisted, so future refreshes keep tracking
    /// the usage-progress bar live against it without asking again, and the cache read weight can be
    /// tuned without reading the percentage again - until the user recalibrates (e.g. because the
    /// account's limit changed).
    /// </summary>
    /// <param name="progressPercentArg">The raw <c>--session-token-progress</c>/<c>--week-token-progress</c> argument, if provided.</param>
    /// <param name="saved">The previously persisted calibration for this window, if any.</param>
    /// <param name="costSoFar">The estimated cost this tool has computed for the window so far, by kind of token.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect, used for the percentage shown as the prompt's default.</param>
    /// <param name="promptLabel">A short label (e.g. "Session", "Weekly") used in the interactive prompt text.</param>
    /// <param name="notices">Receives a plain-text notice when a percentage is not used because it cannot be calibrated against the window.</param>
    /// <param name="inputRedirected">Whether there is no interactive terminal to prompt in; then a missing percentage is simply left unset.</param>
    /// <param name="calibration">The resolved calibration, or <see langword="null"/> if not configured.</param>
    /// <param name="error">An error message describing why resolution failed, if it did.</param>
    /// <returns><see langword="true"/> if resolution succeeded; <see langword="false"/> if an explicit argument was invalid.</returns>
    internal static bool ResolveCalibration(
        double? progressPercentArg, UsageCalibration? saved, CostBreakdown costSoFar, decimal cacheReadWeight,
        string promptLabel, ICollection<string> notices, bool inputRedirected, out UsageCalibration? calibration, out string? error)
    {
        error = null;

        if (progressPercentArg is not null)
        {
            if (ValidatePercentArgument(progressPercentArg) is { } invalid)
            {
                calibration = null;
                error = invalid;
                return false;
            }

            if (!HasCountedCost(costSoFar, cacheReadWeight))
            {
                // A reading taken while nothing is counted cannot give a limit, and must not replace one that works.
                notices.Add(DescribeNothingCounted(promptLabel, costSoFar, cacheReadWeight));
                calibration = saved;
                return true;
            }

            calibration = new UsageCalibration(costSoFar, progressPercentArg.Value);
            return true;
        }

        if (saved is not null || inputRedirected)
        {
            calibration = saved;
            return true;
        }

        calibration = PromptForCalibration(
            current: null, costSoFar, cacheReadWeight, promptLabel, notices,
            $"[yellow]{promptLabel} usage %[/] (from /usage), e.g. 32, Enter to skip:");
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
        if (effectiveSessionReset is not null && effectiveSessionReset <= DateTimeOffset.Now)
        {
            // A persisted reset time that has already elapsed no longer describes the current
            // window - keep using it would show a reset time in the past. Fall through to the
            // estimate/prompt below instead, exactly as documented.
            effectiveSessionReset = null;
        }
        var effectiveWeekResetRaw = weekResetArg ?? saved.WeekResetAt;
        var effectiveWeekReset = weekResetArg is not null
            ? weekResetAt
            : (saved.WeekResetAt is not null && TryParseWeekReset(saved.WeekResetAt, out var savedWeekReset) ? savedWeekReset : null);

        if (weekResetArg is null && effectiveWeekReset is null && saved.WeekResetAt is { } unusableSavedWeekReset)
        {
            AnsiConsole.MarkupLine(ConsoleMarkup.Colored(
                "yellow", $"The saved weekly reset '{unusableSavedWeekReset}' is not a valid 'Ddd HH:mm' value (e.g. 'Mon 09:00') and was ignored."));
        }

        var promptedForAnything = false;

        // A persisted reset time that has already elapsed is deliberately not prompted for again:
        // it simply stops applying, the window falls back to an estimate or to "unknown", and the
        // limits view tells the user how to recalibrate without blocking startup. The session reset
        // is likewise never prompted for at startup: without a value it is auto-computed, and can
        // still be set via the 'r' recalibration hotkey.
        if (effectiveWeekReset is null && !Console.IsInputRedirected)
        {
            (effectiveWeekReset, effectiveWeekResetRaw) = PromptForWeekReset(currentRaw: null);
            promptedForAnything = true;
        }

        if (explicitlyProvided || promptedForAnything)
        {
            // Token limits are resolved separately (see ResolveCalibration), since deriving them needs
            // the tokens counted for the window - which isn't known until after the reset times below
            // are settled. Carry the currently saved values through unchanged.
            LimitsSettingsStore.SaveOrWarn(saved with
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
    /// Checks a <c>--session-token-progress</c>/<c>--week-token-progress</c> argument without resolving anything, so that both can be
    /// checked before the first one prompts for anything.
    /// </summary>
    /// <param name="percent">The raw argument, if provided.</param>
    /// <returns>An error message if the argument is given and is not a usable percentage; otherwise <see langword="null"/>.</returns>
    internal static string? ValidatePercentArgument(double? percent)
        => percent is { } value && !UsageCalibration.IsValidPercent(value)
            ? string.Create(CultureInfo.InvariantCulture, $"Invalid usage percentage '{value}'. {PercentRangeHint}")
            : null;

    /// <summary>
    /// Determines whether a window has any cost that counts toward the quota, which a usage percentage needs to be
    /// calibrated against.
    /// </summary>
    /// <param name="costSoFar">The estimated cost accrued for the window so far, by kind of token.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect.</param>
    /// <returns><see langword="true"/> if the weighted cost is above zero.</returns>
    private static bool HasCountedCost(CostBreakdown costSoFar, decimal cacheReadWeight)
        => QuotaCost.Compute(costSoFar, cacheReadWeight) > 0m;

    /// <summary>
    /// Explains why a usage percentage cannot be calibrated against a window that has no cost counting toward the quota.
    /// The cause is either that the window has no usage yet, or that the cache read weight makes all of its usage count as zero.
    /// </summary>
    /// <param name="label">A short label (e.g. "Session", "Weekly") naming the window.</param>
    /// <param name="costSoFar">The estimated cost accrued for the window so far, by kind of token.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect.</param>
    /// <returns>The plain-text sentence.</returns>
    internal static string DescribeNothingCounted(string label, CostBreakdown costSoFar, decimal cacheReadWeight)
    {
        var hasUsage = costSoFar.Total > 0m;
        var reason = hasUsage
            ? $"all of its usage counts as zero under a cache read weight of {cacheReadWeight:0.##}; raise --cache-read-weight to calibrate"
            : "nothing has been counted for it yet; try again once it has usage";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: the usage percentage was not used, because {reason}. The saved reading, if any, is kept.");
    }

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
        var defaultText = currentValue is { } v ? v.ToString(SessionResetFormat, CultureInfo.InvariantCulture) : "";
        var promptText = currentValue is not null
            ? $"[yellow]Session reset time[/] (local '{SessionResetFormat}', e.g. 18:30), Enter to use the shown value:"
            : $"[yellow]Session reset time[/] (local '{SessionResetFormat}', e.g. 18:30), Enter to estimate:";

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

            AnsiConsole.MarkupLine(ConsoleMarkup.Red(parseError ?? ""));
        }
    }

    /// <summary>
    /// Prompts for a usage percentage and pairs it with <paramref name="costSoFar"/> as a new calibration.
    /// The prompt's default is the percentage <paramref name="current"/> currently implies (given
    /// <paramref name="costSoFar"/>), so pressing Enter keeps the existing calibration unchanged rather
    /// than re-deriving it from a rounded default percentage. A blank answer keeps it too, and so does a percentage
    /// typed while nothing is counted for the window, which could not be calibrated against (the reason goes to
    /// <paramref name="notices"/>).
    /// </summary>
    /// <param name="current">The currently configured calibration, if any.</param>
    /// <param name="costSoFar">The estimated cost accrued for the window so far, by kind of token.</param>
    /// <param name="cacheReadWeight">The cache read weight in effect.</param>
    /// <param name="label">A short label (e.g. "Session", "Weekly") naming the window in notices.</param>
    /// <param name="notices">Receives a plain-text notice when a typed percentage is not used.</param>
    /// <param name="promptText">The prompt text to display.</param>
    private static UsageCalibration? PromptForCalibration(
        UsageCalibration? current, CostBreakdown costSoFar, decimal cacheReadWeight, string label,
        ICollection<string> notices, string promptText)
    {
        var defaultText = current?.PercentOf(costSoFar, cacheReadWeight) is { } implied
            ? implied.ToString("0.##", CultureInfo.InvariantCulture)
            : "";

        while (true)
        {
            var input = Ask(promptText, defaultText);

            // Enter always keeps what is there, even when no percentage can be shown as the default.
            if (string.IsNullOrWhiteSpace(input) || (input == defaultText && current is not null))
            {
                return current;
            }

            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                && UsageCalibration.IsValidPercent(parsed))
            {
                if (!HasCountedCost(costSoFar, cacheReadWeight))
                {
                    notices.Add(DescribeNothingCounted(label, costSoFar, cacheReadWeight));
                    return current;
                }

                return new UsageCalibration(costSoFar, parsed);
            }

            AnsiConsole.MarkupLine(ConsoleMarkup.Red($"Invalid value. {PercentRangeHint}"));
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
            ? "[yellow]Weekly reset time[/] ('Ddd HH:mm', e.g. 'Mon 09:00'), Enter to keep:"
            : "[yellow]Weekly reset time[/] ('Ddd HH:mm', e.g. 'Mon 09:00'), Enter to estimate:";

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

    /// <summary>
    /// Parses a day name or abbreviation (e.g. <c>Mon</c>, <c>thu</c>, <c>Sunday</c>). A prefix that fits more than
    /// one day (such as <c>S</c> or <c>T</c>) is rejected rather than silently resolved to the first of them.
    /// </summary>
    /// <param name="value">The text to parse.</param>
    /// <param name="day">The day it names.</param>
    /// <returns><see langword="true"/> if the text names exactly one day.</returns>
    private static bool TryParseDayOfWeek(string value, out DayOfWeek day)
    {
        var matches = Enum.GetValues<DayOfWeek>()
            .Where(candidate => candidate.ToString().StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        day = matches.Count == 1 ? matches[0] : default;

        return matches.Count == 1;
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