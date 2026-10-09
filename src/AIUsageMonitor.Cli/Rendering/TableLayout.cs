namespace AIUsageMonitor.Cli.Rendering;

/// <summary>
/// The facts that decide how the project and session tables are laid out.
/// </summary>
/// <param name="ConsoleWidth">The width of the console, in characters; the project name takes whatever the other columns leave.</param>
/// <param name="ShowMessages">Whether to add a Messages column; it is only shown when the rows are ordered by it, to keep the table narrow.</param>
public readonly record struct TableLayout(int ConsoleWidth, bool ShowMessages);
