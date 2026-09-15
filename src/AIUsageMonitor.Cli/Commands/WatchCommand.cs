using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Represents the "watch" command, which continuously refreshes a usage view at a fixed interval.
/// </summary>
public static class WatchCommand
{
    /// <summary>
    /// Creates the "watch" command with its options and action.
    /// </summary>
    /// <param name="dataService">The data service used to retrieve usage data for the specified view.</param>
    /// <returns>A configured <see cref="Command"/> instance for continuously refreshing a usage view.</returns>
    public static Command Create(DataService dataService)
    {
        var command = new Command("watch", "Continuously refresh a usage view at a fixed interval");
        var viewOption = new Option<string>("--view")
        {
            Description = "View to refresh: limits, today, week, models, sessions, hours",
            DefaultValueFactory = _ => "limits"
        };
        var intervalOption = new Option<int>("--interval")
        {
            Description = "Refresh interval in seconds",
            DefaultValueFactory = _ => 2
        };
        var (sessionResetOption, weekResetOption) = LimitsAnchors.CreateOptions();
        command.Options.Add(viewOption);
        command.Options.Add(intervalOption);
        command.Options.Add(sessionResetOption);
        command.Options.Add(weekResetOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var view = parseResult.GetValue(viewOption)!;
            var interval = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(intervalOption)));
            var sessionResetArg = parseResult.GetValue(sessionResetOption);
            var weekResetArg = parseResult.GetValue(weekResetOption);

            DateTimeOffset? effectiveSessionResetAt = null;
            (DayOfWeek Day, TimeSpan TimeOfDay)? effectiveWeekResetAt = null;
            if (view == "limits")
            {
                if (!LimitsAnchors.TryResolve(sessionResetArg, weekResetArg, out effectiveSessionResetAt, out effectiveWeekResetAt, out var error))
                {
                    AnsiConsole.MarkupLine($"[red]{error}[/]");
                    return 1;
                }
            }

            var recalibrationEnabled = view == "limits" && !Console.IsInputRedirected;

            IRenderable BuildCurrent(IProgress<int>? progress = null) => view switch
            {
                "today" => new Rows(
                    SpectreRenderer.BuildDailySummary(
                        dataService.GetDailySummary(DateOnly.FromDateTime(DateTime.Today), progress)
                            ?? DailySummary.Empty(DateOnly.FromDateTime(DateTime.Today))),
                    new Rule().RuleStyle("grey"),
                    SpectreRenderer.BuildHourlyTokenChart(dataService.GetRecentActivity(DateTimeOffset.Now - DateTimeOffset.Now.Date, progress).HourlyTrend)),
                "week" => SpectreRenderer.BuildPeriodSummary(
                    dataService.GetPeriodSummary(DateOnly.FromDateTime(DateTime.Today).AddDays(-6), DateOnly.FromDateTime(DateTime.Today), progress)),
                "models" => SpectreRenderer.BuildModelDistribution(dataService.GetModelDistribution(progress)),
                "sessions" => SpectreRenderer.BuildSessionStats(dataService.GetSessionStats(progress)),
                "hours" => SpectreRenderer.BuildHourlyActivity(dataService.GetHourlyActivity(progress)),
                "limits" => BuildLimits(progress),
                _ => new Markup($"[red]Unknown view: {view}. Use today|week|models|sessions|hours|limits.[/]")
            };

            IRenderable BuildLimits(IProgress<int>? progress)
            {
                return SpectreRenderer.BuildUsageLimits(
                    dataService.GetCurrentSessionWindow(effectiveSessionResetAt, progress),
                    dataService.GetWeekWindow(effectiveWeekResetAt, progress),
                    recalibrationEnabled);
            }

            ClearScreen();

            var current = ProgressReporter.Run("Loading usage data...", p => BuildCurrent(p));

            while (!ct.IsCancellationRequested)
            {
                var recalibrationRequested = false;

                // Spectre's Live display and its prompts cannot draw at the same time, so the
                // hotkey has to tear the Live display down before asking for the new reset time.
                await AnsiConsole.Live(current)
                    .StartAsync(async ctx =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            ctx.UpdateTarget(BuildCurrent());
                            ctx.Refresh();

                            if (await WaitForRefreshAsync(interval, recalibrationEnabled, ct))
                            {
                                recalibrationRequested = true;
                                return;
                            }
                        }
                    });

                if (!recalibrationRequested)
                {
                    break;
                }

                ClearScreen();
                AnsiConsole.MarkupLine("[grey]Press Enter on either prompt to keep using a local estimate.[/]");
                (effectiveSessionResetAt, effectiveWeekResetAt) = LimitsAnchors.PromptAndSaveBoth();
                ClearScreen();
                current = ProgressReporter.Run("Loading usage data...", p => BuildCurrent(p));
            }

            ClearScreen();

            return 0;
        });

        return command;
    }

    private static void ClearScreen()
    {
        try
        {
            AnsiConsole.Clear();
        }
        catch (IOException)
        {
            // Output is not a real console (piped or redirected), so there is no screen to clear.
        }
    }

    /// <summary>
    /// Waits out the refresh interval, watching for the recalibration hotkey while it does.
    /// </summary>
    /// <returns><see langword="true"/> if the user asked to recalibrate; <see langword="false"/> if the interval simply elapsed or was cancelled.</returns>
    private static async Task<bool> WaitForRefreshAsync(TimeSpan interval, bool recalibrationEnabled, CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromMilliseconds(150);
        var deadline = DateTimeOffset.Now + interval;

        try
        {
            if (!recalibrationEnabled)
            {
                await Task.Delay(interval, ct);
                return false;
            }

            while (!ct.IsCancellationRequested)
            {
                if (RecalibrationKeyPressed())
                {
                    return true;
                }

                var remaining = deadline - DateTimeOffset.Now;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                await Task.Delay(remaining < pollInterval ? remaining : pollInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return false;
    }

    private static bool RecalibrationKeyPressed()
    {
        try
        {
            var pressed = false;
            while (Console.KeyAvailable)
            {
                pressed |= Console.ReadKey(intercept: true).Key == ConsoleKey.R;
            }
            return pressed;
        }
        catch (InvalidOperationException)
        {
            // No console input stream available (e.g. running without a real terminal).
            return false;
        }
    }
}