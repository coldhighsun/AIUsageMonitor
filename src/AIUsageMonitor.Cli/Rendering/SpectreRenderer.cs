using AIUsageMonitor.Core.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace AIUsageMonitor.Cli.Rendering;

public static class SpectreRenderer
{
    public static void RenderDailySummary(DailySummary summary) => AnsiConsole.Write(BuildDailySummary(summary));

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

        var chart = new BarChart().Label("[bold]Tokens by Model[/]").Width(80).UseValueFormatter(v => ((long)v).ToString("N0"));
        var colorIndex = 0;
        var colors = new[] { Color.Blue, Color.Green, Color.Yellow, Color.Red, Color.Purple, Color.Orange1, Color.Cyan1 };
        foreach (var (model, tokens) in summary.TokensByModel.OrderByDescending(x => x.Value))
        {
            chart.AddItem(ShortenModelName(model), tokens, colors[colorIndex % colors.Length]);
            colorIndex++;
        }
        return new Rows(table, new Rule().RuleStyle("grey"), chart);
    }

    public static void RenderPeriodSummary(PeriodSummary summary) => AnsiConsole.Write(BuildPeriodSummary(summary));

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

        var chart = new BarChart().Label("[bold]Daily Tokens[/]").Width(80).UseValueFormatter(v => ((long)v).ToString("N0"));
        var colorIndex = 0;
        foreach (var day in summary.DailyBreakdown)
        {
            chart.AddItem(day.Date.ToString("MM-dd"), day.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return new Rows(table, new Rule().RuleStyle("grey"), chart);
    }

    public static void RenderModelDistribution(List<ModelDistribution> models) => AnsiConsole.Write(BuildModelDistribution(models));

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

    public static void RenderSessionStats(SessionStats stats) => AnsiConsole.Write(BuildSessionStats(stats));

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

    public static void RenderHourlyActivity(List<HourlyActivity> hours) => AnsiConsole.Write(BuildHourlyActivity(hours));

    private static readonly Color[] HourlyBarColors = { Color.Blue, Color.SkyBlue1, Color.Green, Color.Yellow3 };

    public static IRenderable BuildHourlyActivity(List<HourlyActivity> hours)
    {
        var chart = new BarChart().Label("[bold]Tokens by Hour[/]").Width(80).UseValueFormatter(v => ((long)v).ToString("N0"));
        var colorIndex = 0;
        foreach (var h in hours)
        {
            chart.AddItem($"{h.Hour:D2}:00", h.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return chart;
    }

    public static IRenderable BuildHourlyTokenChart(List<HourBucket> buckets)
    {
        var chart = new BarChart().Label("[bold]Tokens by Hour[/]").Width(80).UseValueFormatter(v => ((long)v).ToString("N0"));
        var colorIndex = 0;
        foreach (var bucket in buckets)
        {
            chart.AddItem(bucket.HourStart.ToString("HH:00"), bucket.TotalTokens, HourlyBarColors[colorIndex % HourlyBarColors.Length]);
            colorIndex++;
        }
        return chart;
    }

    public static void RenderRecentActivity(RecentActivitySummary recent) => AnsiConsole.Write(BuildRecentActivity(recent));

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

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:F2}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:F2}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}K",
        _ => tokens.ToString("N0")
    };

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
}
