using AIUsageMonitor.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;
using Xunit;

namespace AIUsageMonitor.Cli.Tests;

/// <summary>
/// Tests for <see cref="ConsoleMarkup"/>.
/// </summary>
public class ConsoleMarkupTests
{
    /// <summary>
    /// Renders a renderable to plain text, without colors or ANSI sequences.
    /// </summary>
    /// <param name="renderable">The renderable to render.</param>
    /// <returns>The rendered text.</returns>
    internal static string RenderPlain(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 200;
        console.Write(renderable);

        return writer.ToString();
    }

    /// <summary>
    /// Verifies that square brackets in the text are shown literally instead of being parsed as markup.
    /// </summary>
    [Fact]
    public void Red_TextWithBrackets_RendersTextVerbatim()
    {
        var markup = ConsoleMarkup.Red("Could not open C:\\data[1]\\out.json [/]");

        var rendered = RenderPlain(new Markup(markup));

        Assert.Contains("C:\\data[1]\\out.json [/]", rendered);
    }

    /// <summary>
    /// Verifies that the color tag itself still wraps the escaped text.
    /// </summary>
    [Fact]
    public void Colored_PlainText_WrapsInColorTag()
    {
        var markup = ConsoleMarkup.Colored("green", "done");

        Assert.Equal("[green]done[/]", markup);
    }
}
