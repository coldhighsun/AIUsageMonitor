using AIUsageMonitor.Cli;
using AIUsageMonitor.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// Builds and renders Spectre.Console renderables (tables, charts, and rows) for usage statistics.
/// </summary>
public static class SpectreRenderer
{
    /// <summary>
    /// Minimum gap between token-used fraction and time-elapsed fraction (in either direction) before
    /// a pace hint is shown. Keeps the hint from firing on noise when the two are roughly in step.
    /// </summary>
    private const double PaceGapThreshold = 0.15;

    /// <summary>
    /// A palette for per-row bar charts (e.g. daily tokens) where every consecutive pair - including
    /// the wrap-around from the last color back to the first - must be visually distinct, since
    /// adjacent rows are told apart mainly by bar color.
    /// </summary>
    private static readonly Color[] DailyBarColors =
        [Color.Blue, Color.Red, Color.Green, Color.Gold1, Color.Purple, Color.Cyan1];

    private static readonly Color[] HourlyBarColors = [Color.Blue, Color.SkyBlue1, Color.Green, Color.Yellow3];

    /// <summary>
    /// Builds a renderable summary for a single day, including key stats and a token distribution chart by model.
    /// </summary>
    /// <param name="summary">The daily summary data to render.</param>
    /// <returns>An <see cref="IRenderable"/> representing the daily summary.</returns>
    public static IRenderable BuildDailySummary(DailySummary summary)
    {
        var table = BuildStatsTable(
            $"{summary.Date:yyyy-MM-dd} Summary",
            ("Messages", $"{summary.Messages:N0}"),
            ("Sessions", $"{summary.Sessions:N0}"),
            ("Tool Calls", $"{summary.ToolCalls:N0}"),
            ("Total Tokens", FormatTokens(summary.TotalTokens)),
            ("Est. Cost", $"{summary.EstimatedCost:C2}"));

        if (summary.TokensByModel.Count == 0)
        {
            return table;
        }

        var chart = new BarChart().Label("[bold]Tokens by Model[/]").Width(80).UseValueFormatter(v => FormatTokens((long)v));
        var colorIndex = 0;
        foreach (var (model, tokens) in summary.TokensByModel.OrderByDescending(x => x.Value))
        {
            chart.AddItem(ShortenModelName(model), tokens, DailyBarColors[colorIndex % DailyBarColors.Length]);
            colorIndex++;
        }
        return new Rows(table, new Rule().RuleStyle("grey"), chart);
    }

    /// <summary>
    /// Builds a bar chart of tokens consumed per hour.
    /// </summary>
    /// <param name="hours">The hourly activity data to render.</param>
    /// <returns>An <see cref="IRenderable"/> bar chart of tokens by hour.</returns>
    public static IRenderable BuildHourlyActivity(List<HourlyActivity> hours)
    {
        var chart = new BarChart().Label("[bold]Tokens by Hour[/]").Width(80).UseValueFormatter(v => FormatTokens((long)v));
        var colorIndex = 0;
        foreach (var h in hours)
        {
            chart.AddItem($"{h.Hour:D2}:00", h.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return chart;
    }

    /// <summary>
    /// Builds a bar chart of tokens consumed per hour from hour buckets.
    /// </summary>
    /// <param name="buckets">The hour buckets containing token totals.</param>
    /// <returns>An <see cref="IRenderable"/> bar chart of tokens by hour.</returns>
    public static IRenderable BuildHourlyTokenChart(List<HourBucket> buckets)
    {
        var chart = new BarChart().Label("[bold]Tokens by Hour[/]").Width(80).UseValueFormatter(v => FormatTokens((long)v));
        var colorIndex = 0;
        foreach (var bucket in buckets)
        {
            chart.AddItem(bucket.HourStart.ToString("HH:00"), bucket.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return chart;
    }

    /// <summary>
    /// Builds a table showing token usage and cost breakdown per model.
    /// </summary>
    /// <param name="models">The per-model distribution data to render.</param>
    /// <returns>An <see cref="IRenderable"/> table of model token distribution.</returns>
    public static IRenderable BuildModelDistribution(List<ModelDistribution> models)
    {
        var table = new Table();
        table.AddColumn("Model");
        table.AddColumn(new TableColumn("Input").RightAligned());
        table.AddColumn(new TableColumn("Output").RightAligned());
        table.AddColumn(new TableColumn("Cache Read").RightAligned());
        table.AddColumn(new TableColumn("Cache Write").RightAligned());
        table.AddColumn(new TableColumn("Total").RightAligned());
        table.AddColumn(new TableColumn("%").RightAligned());
        table.AddColumn(new TableColumn("Est. Cost").RightAligned());

        foreach (var m in models)
        {
            table.AddRow(
                m.ModelName,
                FormatTokens(m.InputTokens),
                FormatTokens(m.OutputTokens),
                FormatTokens(m.CacheReadTokens),
                FormatTokens(m.CacheCreationTokens),
                FormatTokens(m.TotalTokens),
                $"{m.Percentage:F1}%",
                $"${m.EstimatedCost:F2}");
        }

        return table;
    }

    /// <summary>
    /// Builds a renderable summary for a date range, including key stats and a daily token chart.
    /// </summary>
    /// <param name="summary">The period summary data to render.</param>
    /// <returns>An <see cref="IRenderable"/> representing the period summary.</returns>
    public static IRenderable BuildPeriodSummary(PeriodSummary summary)
    {
        var table = BuildStatsTable(
            $"{summary.From:yyyy-MM-dd} ~ {summary.To:yyyy-MM-dd}",
            ("Messages", $"{summary.TotalMessages:N0}"),
            ("Sessions", $"{summary.TotalSessions:N0}"),
            ("Tool Calls", $"{summary.TotalToolCalls:N0}"),
            ("Total Tokens", FormatTokens(summary.TotalTokens)),
            ("Est. Cost", $"{summary.EstimatedCost:C2}"));

        if (summary.DailyBreakdown.Count == 0)
        {
            return table;
        }

        var chart = new BarChart().Label("[bold]Daily Tokens[/]").Width(80).UseValueFormatter(v => FormatTokens((long)v));
        for (var i = 0; i < summary.DailyBreakdown.Count; i++)
        {
            var day = summary.DailyBreakdown[i];
            chart.AddItem(day.Date.ToString("MM-dd"), day.TotalTokens, DailyBarColors[i % DailyBarColors.Length]);
        }
        return new Rows(table, new Rule().RuleStyle("grey"), chart);
    }

    /// <summary>
    /// Builds a renderable summary of recent activity, including key stats and a message-by-hour trend chart.
    /// </summary>
    /// <param name="recent">The recent activity summary data to render.</param>
    /// <returns>An <see cref="IRenderable"/> representing the recent activity summary.</returns>
    public static IRenderable BuildRecentActivity(RecentActivitySummary recent)
    {
        var hours = (int)Math.Round(recent.Window.TotalHours);
        var table = BuildStatsTable(
            $"Last {hours}h Activity",
            ("Messages", $"{recent.Messages:N0}"),
            ("Sessions", $"{recent.Sessions:N0}"),
            ("Tool Calls", $"{recent.ToolCalls:N0}"),
            ("Total Tokens", FormatTokens(recent.TotalTokens)),
            ("Est. Cost", $"{recent.EstimatedCost:C2}"));

        if (recent.HourlyTrend.Count == 0)
        {
            return table;
        }

        var chart = new BarChart().Label("[bold]Messages by Hour[/]").Width(80);
        var colorIndex = 0;
        foreach (var bucket in recent.HourlyTrend)
        {
            chart.AddItem(bucket.HourStart.ToString("HH:00"), bucket.Messages, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return new Rows(table, chart);
    }

    /// <summary>
    /// Builds a renderable summary of session statistics.
    /// </summary>
    /// <param name="stats">The session statistics data to render.</param>
    /// <returns>An <see cref="IRenderable"/> representing the session stats.</returns>
    public static IRenderable BuildSessionStats(SessionStats stats)
    {
        var table = BuildStatsTable(
            "Session Stats",
            ("Total Sessions", $"{stats.Total:N0}"),
            ("Avg Messages/Session", $"{stats.AvgMessages:F1}"),
            ("Longest Session", FormatDuration(stats.LongestDuration)));

        if (stats.LongestSessionId is null)
        {
            return table;
        }

        return new Rows(table, new Markup($"[grey]Longest Session ID: {stats.LongestSessionId}[/]"));
    }

    /// <summary>
    /// Builds a single renderable table summarizing both rate-limit-style usage windows
    /// (the current session and the weekly window), one row per window, followed by a note
    /// explaining any row that is locally estimated ('~') or could not be determined ('?'),
    /// and (while the recalibration hotkey is available) a standing reminder of it.
    /// </summary>
    /// <param name="sessionWindow">The current 5-hour session window data to render.</param>
    /// <param name="weekWindow">The current weekly window data to render.</param>
    /// <param name="sessionTokenLimit">
    /// The account's session usage limit, expressed as an estimated USD cost budget, or
    /// <see langword="null"/> if not configured (in which case the usage-progress column is left
    /// blank for that row). Cost already weights tokens by model and type, which tracks Anthropic's
    /// real usage gating far more closely than a flat token count. Claude itself only ever shows a
    /// usage *percentage*, never this limit, so it is derived once from a percentage the user read
    /// off Claude's usage display - see <see cref="Commands.LimitsAnchors.ResolveTokenLimit"/> - and
    /// then used here to track <paramref name="sessionWindow"/>'s live estimated cost against it.
    /// </param>
    /// <param name="weekTokenLimit">The account's weekly usage (cost) limit, or <see langword="null"/> if not configured.</param>
    /// <param name="recalibrationAvailable">
    /// Whether the "press r" hotkey hint applies, i.e. whether the caller is actually watching for
    /// it (it does nothing outside an interactive <c>watch</c> session).
    /// </param>
    /// <param name="sessionResetConfigured">
    /// Whether a real session reset time is currently configured (e.g. via <c>--session-reset</c>
    /// or a prior <c>r</c> prompt), regardless of whether it has since elapsed. Used to tell an
    /// idle account (no reset time known at all) apart from a session that just reset and is
    /// waiting for its first message.
    /// </param>
    /// <returns>An <see cref="IRenderable"/> representing both usage windows.</returns>
    public static IRenderable BuildUsageLimits(
        UsageWindowSummary sessionWindow, UsageWindowSummary weekWindow,
        decimal? sessionTokenLimit = null, decimal? weekTokenLimit = null, bool recalibrationAvailable = true,
        bool sessionResetConfigured = false)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold yellow]Usage Limits[/]").ShowRowSeparators();
        table.AddColumn(HeaderColumn("Window", Justify.Left));
        table.AddColumn(HeaderColumn("Tokens", Justify.Right).NoWrap());
        table.AddColumn(HeaderColumn("Messages", Justify.Right).NoWrap());
        table.AddColumn(HeaderColumn("Cost", Justify.Right).NoWrap());
        table.AddColumn(HeaderColumn("Resets At", Justify.Right).NoWrap());
        table.AddColumn(HeaderColumn("Time Left", Justify.Right).NoWrap());
        table.AddColumn(HeaderColumn("Time Progress", Justify.Center));
        table.AddColumn(HeaderColumn("Usage Progress", Justify.Center));

        var sessionFractions = AddRow(table, "Session (5h)", sessionWindow, sessionTokenLimit);
        AddRow(table, "Week", weekWindow, weekTokenLimit);

        var notes = new List<IRenderable> { table };

        if (sessionFractions is { } fractions && FormatPaceHint(fractions.Time, fractions.Token) is { } paceHint)
        {
            notes.Add(paceHint);
        }

        if (sessionWindow.Confidence is WindowConfidence.Unknown)
        {
            notes.Add(new Markup(sessionResetConfigured
                ? "[grey]⏳ Waiting for a new session to start — the previous 5h window has reset and no "
                  + "activity has been seen yet on this machine.[/]"
                : "[grey]? Session window unknown — no local activity in the last 5 hours. Your account may be idle, or "
                  + "in use on another device this machine can't see.[/]"));
        }

        if (sessionWindow.Confidence is WindowConfidence.Estimated)
        {
            notes.Add(new Markup(
                "[grey]~ Session estimated from this machine's activity only — the real window may have started "
                + "earlier on another device, so the actual reset can come sooner.[/]"));
        }

        if (weekWindow.Confidence is WindowConfidence.Unknown)
        {
            notes.Add(new Markup(
                "[grey]? Weekly reset time not set — showing trailing 7-day usage as an upper bound on the current "
                + "cycle. Your weekly reset is a fixed time assigned to your account (see Settings > Usage, or "
                + "/usage in Claude Code).[/]"));
        }

        if (recalibrationAvailable)
        {
            notes.Add(new Markup("[grey]Press [[r]] to enter or update the reset times Claude shows.[/]"));
        }

        return notes.Count == 1 ? table : new Rows(notes);

        static (double Time, double Token)? AddRow(Table table, string label, UsageWindowSummary window, decimal? costLimit)
        {
            var (tokens, messages, cost) = window.WindowStart is null
                ? ("—", "—", "—")
                : (FormatTokens(window.TotalTokens), $"{window.Messages:N0}", $"{window.EstimatedCost:C2}");

            var tokenFraction = window.WindowStart is not null && costLimit is { } limit and > 0
                ? (double)(window.EstimatedCost / limit)
                : (double?)null;
            var tokenProgress = tokenFraction is { } tf ? FormatProgressBar(tf) : "—";

            if (window.ResetsAt is not { } resetsAt)
            {
                table.AddRow(label, tokens, messages, cost, "?", "unknown", "—", tokenProgress);
                return null;
            }

            var now = DateTimeOffset.Now;
            var remaining = resetsAt > now ? resetsAt - now : TimeSpan.Zero;
            var marker = window.Confidence is WindowConfidence.Estimated ? "~" : "";

            var timeFraction = window.WindowStart is { } windowStart && resetsAt > windowStart
                ? (now - windowStart).Ticks / (double)(resetsAt - windowStart).Ticks
                : (double?)null;
            var timeProgress = timeFraction is { } tp ? FormatPaceProgressBar(tp, tokenFraction) : "—";

            table.AddRow(
                label,
                tokens,
                messages,
                cost,
                $"{marker}{resetsAt:MM-dd HH:mm}",
                FormatDuration(remaining),
                timeProgress,
                tokenProgress);

            return window.Confidence is WindowConfidence.Unknown || timeFraction is null || tokenFraction is null
                ? null
                : (timeFraction.Value, tokenFraction.Value);
        }
    }

    /// <summary>
    /// Renders the daily summary directly to the console.
    /// </summary>
    /// <param name="summary">The daily summary data to render.</param>
    public static void RenderDailySummary(DailySummary summary) => AnsiConsole.Write(BuildDailySummary(summary));

    /// <summary>
    /// Renders the hourly activity chart directly to the console.
    /// </summary>
    /// <param name="hours">The hourly activity data to render.</param>
    public static void RenderHourlyActivity(List<HourlyActivity> hours) => AnsiConsole.Write(BuildHourlyActivity(hours));

    /// <summary>
    /// Renders the model distribution table directly to the console.
    /// </summary>
    /// <param name="models">The per-model distribution data to render.</param>
    public static void RenderModelDistribution(List<ModelDistribution> models) => AnsiConsole.Write(BuildModelDistribution(models));

    /// <summary>
    /// Renders the period summary directly to the console.
    /// </summary>
    /// <param name="summary">The period summary data to render.</param>
    public static void RenderPeriodSummary(PeriodSummary summary) => AnsiConsole.Write(BuildPeriodSummary(summary));

    /// <summary>
    /// Renders the recent activity summary directly to the console.
    /// </summary>
    /// <param name="recent">The recent activity summary data to render.</param>
    public static void RenderRecentActivity(RecentActivitySummary recent) => AnsiConsole.Write(BuildRecentActivity(recent));

    /// <summary>
    /// Renders the session stats summary directly to the console.
    /// </summary>
    /// <param name="stats">The session statistics data to render.</param>
    public static void RenderSessionStats(SessionStats stats) => AnsiConsole.Write(BuildSessionStats(stats));

    /// <summary>
    /// Builds a footer line naming the application and its running version, for display under
    /// the continuously refreshed <c>watch</c> view only - a one-shot command's output is short
    /// enough that the version line would just be noise under it.
    /// </summary>
    /// <returns>A grey markup line, or <see langword="null"/> if the version could not be determined.</returns>
    public static IRenderable? BuildVersionFooter()
    {
        var version = UpdateChecking.GetCurrentVersionDisplayString();
        return version is null ? null : new Markup($"[grey]aimon v{version} · https://github.com/coldhighsun/AIUsageMonitor[/]");
    }

    /// <summary>
    /// Builds a titled table with a single row of centered stat values, one column per stat.
    /// </summary>
    /// <param name="title">The table title.</param>
    /// <param name="stats">The label/value pairs to render as columns and their values.</param>
    /// <returns>A configured <see cref="Table"/> with the stats rendered.</returns>
    private static Table BuildStatsTable(string title, params (string Label, string Value)[] stats)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold yellow]{title}[/]").Width(80);
        foreach (var (label, _) in stats)
        {
            table.AddColumn(new TableColumn($"[grey]{label}[/]").Centered());
        }
        table.AddRow(stats.Select(s => (IRenderable)new Markup($"[bold aqua]{s.Value}[/]").Centered()).ToArray());
        return table;
    }

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> into a compact human-readable duration string.
    /// </summary>
    /// <param name="ts">The duration to format.</param>
    /// <returns>A human-readable representation of the duration.</returns>
    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{ts.Days}d {ts.Hours}h {ts.Minutes}m";
        }
        if (ts.TotalHours >= 1)
        {
            return $"{ts.Hours}h {ts.Minutes}m";
        }
        return $"{ts.Minutes}m {ts.Seconds}s";
    }

    /// <summary>
    /// Renders the fixed-width text progress bar for the time-progress column, colored by how usage
    /// pace compares to time pace rather than by how full the bar itself is - time ticking forward has
    /// no "bad" value on its own, but how it compares to token usage does. Green when usage is pacing
    /// behind time (headroom to spare), orange when usage is pacing ahead of time (running hot), grey
    /// when the two are within <see cref="PaceGapThreshold"/> of each other or token usage isn't known.
    /// </summary>
    private static string FormatPaceProgressBar(double timeFraction, double? tokenFraction)
    {
        if (tokenFraction is not { } tf)
        {
            return FormatProgressBar(timeFraction, "grey");
        }

        var gap = tf - timeFraction;
        var color = gap switch
        {
            <= -PaceGapThreshold => "green",
            >= PaceGapThreshold => "orange3",
            _ => "grey"
        };

        return FormatProgressBar(timeFraction, color);
    }

    /// <summary>
    /// Compares how far the session window has progressed in time versus in token usage, and returns a
    /// note suggesting the user speed up (usage lagging behind time) or slow down (usage running ahead
    /// of time). Returns <c>null</c> when the gap is within <see cref="PaceGapThreshold"/> (pace looks
    /// fine, no need to say anything) or the window just started (too little data to be meaningful).
    /// </summary>
    private static Markup? FormatPaceHint(double timeFraction, double tokenFraction)
    {
        if (timeFraction < 0.05)
        {
            return null;
        }

        var gap = tokenFraction - timeFraction;
        return gap switch
        {
            <= -PaceGapThreshold => new Markup("[green]🐢 Usage is pacing behind time — plenty of headroom to use more.[/]"),
            >= PaceGapThreshold => new Markup("[orange3]🐇 Usage is pacing ahead of time — consider slowing down.[/]"),
            _ => null
        };
    }

    /// <summary>
    /// Renders a fixed-width text progress bar (e.g. <c>[███████████████░░░░░] 72%</c>) for a 0-1
    /// fraction, colored green/yellow/orange/red by how full it is so low/medium/high usage is
    /// visible at a glance. Used for the token-progress column, where the fraction is a genuine
    /// "how close to the limit" status. A fraction at or beyond 1 renders as a full, red bar with the
    /// actual percentage (which can exceed 100%) rather than being silently capped.
    /// </summary>
    private static string FormatProgressBar(double fraction)
    {
        var color = fraction switch
        {
            >= 1.0 => "red",
            >= 0.85 => "orange3",
            >= 0.5 => "yellow",
            _ => "green"
        };

        return FormatProgressBar(fraction, color);
    }

    private static string FormatProgressBar(double fraction, string color)
    {
        const int width = 20;
        var filledCount = Math.Clamp((int)Math.Round(fraction * width), 0, width);
        var bar = new string('█', filledCount) + new string('░', width - filledCount);
        var percentText = $"{fraction * 100:0}%";

        return $"[{color}][[{bar}]] {percentText}[/]";
    }

    /// <summary>
    /// Formats a raw token count into a compact string using B/M/K suffixes.
    /// </summary>
    /// <param name="tokens">The token count to format.</param>
    /// <returns>A compact, human-readable token count string.</returns>
    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:F2}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:F2}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}K",
        _ => tokens.ToString("N0")
    };

    /// <summary>
    /// Creates a <see cref="TableColumn"/> with the given header text. Spectre.Console applies
    /// <see cref="TableColumn.Alignment"/> to the header and its data cells alike (it positions
    /// single-line content within the cell; a <see cref="Markup"/>'s own justification only
    /// affects how wrapped lines align relative to each other), so header and data share the
    /// same alignment here.
    /// </summary>
    private static TableColumn HeaderColumn(string header, Justify alignment) =>
        new(new Markup(header))
        {
            Alignment = alignment
        };

    /// <summary>
    /// Shortens a model name by removing the "claude-" prefix and trailing date suffix, if present.
    /// </summary>
    /// <param name="model">The full model name.</param>
    /// <returns>The shortened model name.</returns>
    private static string ShortenModelName(string model)
    {
        var name = model;
        if (name.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            name = name["claude-".Length..];
        }

        var dashIndex = name.LastIndexOf('-');
        if (dashIndex > 0 && name.Length - dashIndex - 1 == 8 && name[(dashIndex + 1)..].All(char.IsDigit))
        {
            name = name[..dashIndex];
        }

        return name;
    }
}