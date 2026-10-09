using System.CommandLine;

namespace AIUsageMonitor.Cli.Commands;

/// <summary>
/// The values of the list options a user supplied.
/// </summary>
/// <param name="Top">The most rows to show, or <see langword="null"/> for all.</param>
/// <param name="Sort">The sort key, in lower case.</param>
/// <param name="Project">The project filter, trimmed, or <see langword="null"/> when not given.</param>
/// <param name="Model">The model filter, trimmed, or <see langword="null"/> when not given.</param>
internal sealed record ListRequest(int? Top, string Sort, string? Project, string? Model);

/// <summary>
/// Defines the options shared by the project and session lists: <c>--top</c>, <c>--sort</c>, <c>--project</c> and <c>--model</c>.
/// </summary>
internal sealed class ListOptions
{
    /// <summary>
    /// The <c>--top</c> option.
    /// </summary>
    private readonly Option<int?> _top;

    /// <summary>
    /// The <c>--sort</c> option.
    /// </summary>
    private readonly Option<string> _sort;

    /// <summary>
    /// The <c>--project</c> option.
    /// </summary>
    private readonly Option<string?> _project;

    /// <summary>
    /// The <c>--model</c> option.
    /// </summary>
    private readonly Option<string?> _model;

    /// <summary>
    /// Creates the options and adds them to <paramref name="command"/>.
    /// </summary>
    /// <param name="command">The command that receives the options.</param>
    /// <param name="sortKeys">The sort keys the command accepts.</param>
    /// <param name="defaultSort">The sort key used when <c>--sort</c> is not given.</param>
    public ListOptions(Command command, string[] sortKeys, string defaultSort)
    {
        _top = new Option<int?>("--top") { Description = "Only show the first N rows" };
        _top.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() < 1)
            {
                result.AddError("--top must be at least 1.");
            }
        });

        _sort = new Option<string>("--sort")
        {
            Description = $"Order, largest first: {string.Join(", ", sortKeys)}",
            DefaultValueFactory = _ => defaultSort,
        };
        _sort.Validators.Add(result =>
        {
            var value = result.GetValueOrDefault<string>();
            if (!sortKeys.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.AddError($"Unsupported sort '{value}'; expected one of: {string.Join(", ", sortKeys)}.");
            }
        });

        _project = new Option<string?>("--project")
        {
            Description = "Only projects whose path or folder name contains this text (case-insensitive)"
        };
        _model = new Option<string?>("--model")
        {
            Description = "Only count usage of models whose name contains this text (case-insensitive)"
        };

        command.Options.Add(_top);
        command.Options.Add(_sort);
        command.Options.Add(_project);
        command.Options.Add(_model);
    }

    /// <summary>
    /// Reads the option values the user supplied.
    /// </summary>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <returns>The list request.</returns>
    public ListRequest Read(ParseResult parseResult)
    {
        return new ListRequest(
            parseResult.GetValue(_top),
            parseResult.GetValue(_sort)!.ToLowerInvariant(),
            Trimmed(parseResult.GetValue(_project)),
            Trimmed(parseResult.GetValue(_model)));
    }

    /// <summary>
    /// Determines whether the user typed any of these options, as opposed to leaving them at their defaults.
    /// </summary>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <returns><see langword="true"/> when at least one option was given on the command line.</returns>
    public bool AnySupplied(ParseResult parseResult)
    {
        return Supplied(parseResult, _top)
            || Supplied(parseResult, _sort)
            || Supplied(parseResult, _project)
            || Supplied(parseResult, _model);
    }

    /// <summary>
    /// Determines whether an option appeared on the command line.
    /// </summary>
    /// <param name="parseResult">The parse result of the invoked command.</param>
    /// <param name="option">The option to look for.</param>
    /// <returns><see langword="true"/> when the user typed the option.</returns>
    private static bool Supplied(ParseResult parseResult, Option option)
    {
        return parseResult.GetResult(option) is { Implicit: false };
    }

    /// <summary>
    /// Trims a filter text and turns blank text into <see langword="null"/>.
    /// </summary>
    /// <param name="text">The text given by the user.</param>
    /// <returns>The trimmed text, or <see langword="null"/> when it was missing or blank.</returns>
    private static string? Trimmed(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
