using AIUsageMonitor.UpdateCheck;
using Spectre.Console;

namespace AIUsageMonitor.Cli;

/// <summary>
/// Prints a notice pointing to the latest GitHub release when a newer version than the one
/// currently running is available.
/// </summary>
public static class UpdateNotice
{
    /// <summary>
    /// Checks for an update and, if one is available, prints a notice.
    /// </summary>
    /// <param name="updateChecker">The update checker to query.</param>
    public static async Task PrintIfAvailableAsync(IUpdateChecker updateChecker)
    {
        var result = await updateChecker.CheckForUpdateAsync();
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
    public static Spectre.Console.Rendering.IRenderable? BuildRenderable(UpdateCheckResult? result)
    {
        return result is { IsUpdateAvailable: true }
            ? new Markup($"[yellow]A new version ({result.LatestVersion}) of aimon is available. Download it at {result.ReleaseUrl}[/]")
            : null;
    }
}
