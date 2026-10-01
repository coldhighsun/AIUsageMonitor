using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Prints a notice pointing to the latest GitHub release when a newer version than the one
/// currently running is available.
/// </summary>
public static class UpdateNotice
{
    /// <summary>
    /// Starts an update check on a background thread so it runs alongside the command rather than after it.
    /// </summary>
    /// <param name="cancellationToken">A token that abandons the check (e.g. once the command finished and the wait ran out).</param>
    /// <param name="logger">An optional logger used to record a failed update check.</param>
    /// <returns>A task that completes with the outcome of the check.</returns>
    public static Task<UpdateCheckOutcome> StartCheck(CancellationToken cancellationToken, ILogger? logger = null)
    {
        return Task.Run(() => UpdateChecking.CheckForUpdateAsync(cancellationToken, logger), CancellationToken.None);
    }

    /// <summary>
    /// Waits briefly for a check started with <see cref="StartCheck"/> and, if it found an update, prints a notice.
    /// A check that is still running after <paramref name="maxWait"/> is left behind so a slow network never delays exit.
    /// </summary>
    /// <param name="check">The running check.</param>
    /// <param name="maxWait">The longest to wait for the check to finish.</param>
    /// <returns>A task that completes once the notice has been printed or the wait has been given up.</returns>
    public static async Task PrintIfAvailableAsync(Task<UpdateCheckOutcome> check, TimeSpan maxWait)
    {
        if (await Task.WhenAny(check, Task.Delay(maxWait)) != check)
        {
            return;
        }

        // The notice is a courtesy and must never fail the command that already succeeded.
        UpdateCheckOutcome result;
        try
        {
            result = await check;
        }
        catch (Exception)
        {
            return;
        }

        if (result.IsUpdateAvailable)
        {
            // Written to stderr, not stdout, so it never mixes into piped command output (e.g.
            // `aimon export --format json` writing JSON to stdout for a script to consume).
            var errorConsole = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) });
            errorConsole.MarkupLine(
                $"[yellow]A new version ({result.LatestVersion}) of aimon is available. Download it at {result.ReleaseUrl}[/]");
        }
    }

    /// <summary>
    /// Builds the update notice as a renderable line, for embedding into a continuously refreshed view.
    /// </summary>
    /// <param name="result">The update check result to render.</param>
    /// <returns>A markup line if an update is available; otherwise <see langword="null"/>.</returns>
    public static Spectre.Console.Rendering.IRenderable? BuildRenderable(UpdateCheckOutcome? result)
    {
        return result is { IsUpdateAvailable: true }
            ? new Markup($"[yellow]A new version ({result.LatestVersion}) of aimon is available. Download it at {result.ReleaseUrl}[/]")
            : null;
    }
}
