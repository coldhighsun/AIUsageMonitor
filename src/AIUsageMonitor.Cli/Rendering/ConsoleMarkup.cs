using Spectre.Console;

namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// Builds Spectre.Console markup strings around dynamic text, escaping it so that characters such as
/// <c>[</c> in paths, user input or exception messages cannot be mistaken for markup tags.
/// </summary>
internal static class ConsoleMarkup
{
    /// <summary>
    /// Wraps <paramref name="text"/> in the given color tag, escaping it first.
    /// </summary>
    /// <param name="color">The Spectre.Console color name, e.g. <c>red</c>.</param>
    /// <param name="text">The literal text to display.</param>
    /// <returns>A markup string that renders <paramref name="text"/> verbatim in <paramref name="color"/>.</returns>
    public static string Colored(string color, string text)
    {
        return $"[{color}]{Markup.Escape(text)}[/]";
    }

    /// <summary>
    /// Wraps <paramref name="text"/> in a red tag, escaping it first.
    /// </summary>
    /// <param name="text">The literal text to display.</param>
    /// <returns>A markup string that renders <paramref name="text"/> verbatim in red.</returns>
    public static string Red(string text)
    {
        return Colored("red", text);
    }
}
