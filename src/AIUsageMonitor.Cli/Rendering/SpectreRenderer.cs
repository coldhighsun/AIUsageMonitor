using AIUsageMonitor.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// Builds and renders Spectre.Console renderables (tables, charts, and rows) for usage statistics.
/// </summary>
public static class SpectreRenderer
{
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
        var colors = new[] { Color.Blue, Color.Green, Color.Yellow, Color.Red, Color.Purple, Color.Orange1, Color.Cyan1 };
        foreach (var (model, tokens) in summary.TokensByModel.OrderByDescending(x => x.Value))
        {
            chart.AddItem(ShortenModelName(model), tokens, colors[colorIndex % colors.Length]);
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
        var colorIndex = 0;
        foreach (var day in summary.DailyBreakdown)
        {
            chart.AddItem(day.Date.ToString("MM-dd"), day.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
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