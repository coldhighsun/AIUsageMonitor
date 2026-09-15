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
        var (sessionTokenProgressOption, weekTokenProgressOption) = LimitsAnchors.CreateTokenProgressOptions();
        command.Options.Add(viewOption);
        command.Options.Add(intervalOption);
        command.Options.Add(sessionResetOption);
        command.Options.Add(weekResetOption);
        command.Options.Add(sessionTokenProgressOption);
        command.Options.Add(weekTokenProgressOption);

        command.SetAction(async (parseResult, ct) =>
        {
            var view = parseResult.GetValue(viewOption)!;
            var interval = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(intervalOption)));
            var sessionResetArg = parseResult.GetValue(sessionResetOption);
            var weekResetArg = parseResult.GetValue(weekResetOption);
            var sessionTokenProgressArg = parseResult.GetValue(sessionTokenProgressOption);
            var weekTokenProgressArg = parseResult.GetValue(weekTokenProgressOption);

            DateTimeOffset? effectiveSessionResetAt = null;
            (DayOfWeek Day, TimeSpan TimeOfDay)? effectiveWeekResetAt = null;
            long? effectiveSessionTokenLimit = null;
            long? effectiveWeekTokenLimit = null;
            UsageWindowSummary? lastSessionWindow = null;
            UsageWindowSummary? lastWeekWindow = null;

            if (view == "limits")
            {
                var saved = LimitsSettingsStore.Load();

                // Usage data is loaded once, up front, before any prompt - both so the "loading" step
                // doesn't land in between the reset-time and token-percentage prompts below, and so
                // that single fetch's result is reused for the token-limit derivation and the first
                // frame rather than being re-fetched. These windows aren't pinned to the real reset
                // times yet (those are still being asked about below), so the very next refresh tick
                // re-fetches them properly pinned.
                (lastSessionWindow, lastWeekWindow) = ProgressReporter.Run("Loading usage data...", p =>
                    (dataService.GetCurrentSessionWindow(null, p), dataService.GetWeekWindow(null, p)));

                if (!LimitsAnchors.TryResolve(
                        sessionResetArg, weekResetArg, out effectiveSessionResetAt, out effectiveWeekResetAt, out var error))
                {
                    AnsiConsole.MarkupLine($"[red]{error}[/]");
                    return 1;
                }

                // Reload, since TryResolve may have just persisted the reset times entered above -
                // reusing the pre-prompt snapshot here would overwrite them with their stale values.
                saved = LimitsSettingsStore.Load();

                if (!LimitsAnchors.ResolveTokenLimit(
                        sessionTokenProgressArg, saved.SessionTokenLimit, lastSessionWindow.TotalTokens, "Session",
                        out effectiveSessionTokenLimit, out error)
                    || !LimitsAnchors.ResolveTokenLimit(
                        weekTokenProgressArg, saved.WeekTokenLimit, lastWeekWindow.TotalTokens, "Weekly",
                        out effectiveWeekTokenLimit, out error))
                {
                    AnsiConsole.MarkupLine($"[red]{error}[/]");
                    return 1;
                }

                LimitsSettingsStore.Save(saved with
                {
                    SessionTokenLimit = effectiveSessionTokenLimit,
                    WeekTokenLimit = effectiveWeekTokenLimit
                });
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
                lastSessionWindow = dataService.GetCurrentSessionWindow(effectiveSessionResetAt, progress);
                lastWeekWindow = dataService.GetWeekWindow(effectiveWeekResetAt, progress);
                return SpectreRenderer.BuildUsageLimits(
                    lastSessionWindow,
                    lastWeekWindow,
                    effectiveSessionTokenLimit,
                    effectiveWeekTokenLimit,
                    recalibrationEnabled,
                    effectiveSessionResetAt is not null);
            }

            ClearScreen();

            var current = view == "limits"
                ? SpectreRenderer.BuildUsageLimits(lastSessionWindow!, lastWeekWindow!, effectiveSessionTokenLimit, effectiveWeekTokenLimit, recalibrationEnabled, effectiveSessionResetAt is not null)
                : ProgressReporter.Run("Loading usage data...", BuildCurrent);

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
                AnsiConsole.MarkupLine("[grey]Press Enter on any prompt to keep using a local estimate or the current value.[/]");
                (effectiveSessionResetAt, effectiveWeekResetAt, effectiveSessionTokenLimit, effectiveWeekTokenLimit) = LimitsAnchors.PromptAndSaveBoth(
                    lastSessionWindow?.TotalTokens ?? 0, lastWeekWindow?.TotalTokens ?? 0);
                ClearScreen();
                current = ProgressReporter.Run("Loading usage data...", BuildCurrent);
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
}