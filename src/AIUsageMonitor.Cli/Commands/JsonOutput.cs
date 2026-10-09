using AIUsageMonitor.Cli.Rendering;
using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// Shared plumbing for the <c>--json</c> option of the report commands.
/// </summary>
internal static class JsonOutput
{
    /// <summary>
    /// The name of the <c>--json</c> option.
    /// </summary>
    private const string OptionName = "--json";

    /// <summary>
    /// Creates the <c>--json</c> option.
    /// </summary>
    /// <returns>A new boolean option.</returns>
    public static Option<bool> CreateOption()
    {
        return new Option<bool>(OptionName) { Description = "Print the report as JSON instead of a formatted table" };
    }

    /// <summary>
    /// Determines whether the invoked command was given <c>--json</c>.
    /// </summary>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <returns><see langword="true"/> when the command has a <c>--json</c> option and it was set.</returns>
    public static bool IsRequested(ParseResult parseResult)
    {
        foreach (var option in parseResult.CommandResult.Command.Options)
        {
            if (option is Option<bool> { Name: OptionName } jsonOption)
            {
                return parseResult.GetValue(jsonOption);
            }
        }

        return false;
    }

    /// <summary>
    /// Loads data, showing a progress bar unless the output is JSON (which must stay machine-readable).
    /// </summary>
    /// <typeparam name="T">The type of the loaded data.</typeparam>
    /// <param name="json">Whether the output will be JSON.</param>
    /// <param name="description">The progress bar caption.</param>
    /// <param name="body">Loads the data, reporting progress to the supplied reporter.</param>
    /// <returns>The loaded data.</returns>
    public static T Load<T>(bool json, string description, Func<IProgress<int>?, T> body)
    {
        return json ? body(null) : ProgressReporter.Run(description, p => body(p));
    }

    /// <summary>
    /// Serializes a report to indented camelCase JSON.
    /// </summary>
    /// <typeparam name="T">The type of the report.</typeparam>
    /// <param name="value">The report.</param>
    /// <param name="typeInfo">The source-generated type metadata for <typeparamref name="T"/>.</param>
    /// <returns>The JSON text.</returns>
    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        return JsonSerializer.Serialize(value, typeInfo);
    }

    /// <summary>
    /// Writes a report as JSON to standard output.
    /// </summary>
    /// <typeparam name="T">The type of the report.</typeparam>
    /// <param name="value">The report.</param>
    /// <param name="typeInfo">The source-generated type metadata for <typeparamref name="T"/>.</param>
    public static void Write<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        Console.WriteLine(Serialize(value, typeInfo));
    }

    /// <summary>
    /// Prints a report as JSON, or hands it to a renderer for the formatted view.
    /// </summary>
    /// <typeparam name="T">The type of the report.</typeparam>
    /// <param name="json">Whether JSON output was requested.</param>
    /// <param name="value">The report.</param>
    /// <param name="typeInfo">The source-generated type metadata for <typeparamref name="T"/>.</param>
    /// <param name="render">Renders the report as a formatted view.</param>
    public static void Emit<T>(bool json, T value, JsonTypeInfo<T> typeInfo, Action<T> render)
    {
        if (json)
        {
            Write(value, typeInfo);
        }
        else
        {
            render(value);
        }
    }
}
