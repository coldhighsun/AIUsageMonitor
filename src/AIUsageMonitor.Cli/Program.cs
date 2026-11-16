using System.CommandLine;
using AIUsageMonitor.Cli;
using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Cli.Rendering;
using AIUsageMonitor.Core.Providers.Claude;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

// The longest the process lingers after a command finished, waiting for the background update check.
var updateCheckMaxWait = TimeSpan.FromSeconds(2);

try
{
    try
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }
    catch (IOException)
    {
        // Output is redirected (e.g. piped to a file); the encoding can't be changed there.
    }

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Warning);
    builder.Services.AddClaudeUsageCore();
    var host = builder.Build();

    var dataService = host.Services.GetRequiredService<DataService>();
    var updateCheckLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();

    var rootCommand = new RootCommand("aimon - AI Usage Monitor");

    rootCommand.Subcommands.Add(TodayCommand.Create(dataService));
    rootCommand.Subcommands.Add(WeekCommand.Create(dataService));
    rootCommand.Subcommands.Add(MonthCommand.Create(dataService));
    rootCommand.Subcommands.Add(ModelsCommand.Create(dataService));
    rootCommand.Subcommands.Add(SessionsCommand.Create(dataService));
    rootCommand.Subcommands.Add(ProjectsCommand.Create(dataService));
    rootCommand.Subcommands.Add(HoursCommand.Create(dataService));
    rootCommand.Subcommands.Add(ExportCommand.Create(dataService));
    var watchCommand = WatchCommand.Create(dataService, updateCheckLogger);
    rootCommand.Subcommands.Add(watchCommand);

    var statuslineCommand = StatuslineCommand.Create(host.Services.GetRequiredService<StatuslineLimitsStore>());
    rootCommand.Subcommands.Add(statuslineCommand);

    var parseResult = rootCommand.Parse(args);
    var isWatch = parseResult.CommandResult.Command == watchCommand;
    var isStatusline = parseResult.CommandResult.Command == statuslineCommand;

    if (isWatch && parseResult.Errors.Count == 0 && !Console.IsOutputRedirected)
    {
        AnsiConsole.Clear();
    }

    // watch runs its own update check up front (it's a long-running loop, so the notice needs to
    // show while it's running, not after it exits) - every other command starts the check here so it
    // runs alongside the command, and prints the notice after the command's own output.
    using var updateCheckCts = new CancellationTokenSource();
    // --json is for scripts, which shouldn't pay a network request or the wait for it on every call.
    // The status line command runs after every response and its output is the status line, so it never checks for updates.
    var updateCheck = UpdateNotice.ShouldCheckAfterCommand(isWatch || isStatusline, JsonOutput.IsRequested(parseResult)) ? UpdateNotice.StartCheck(updateCheckCts.Token, updateCheckLogger) : null;

    var exitCode = await parseResult.InvokeAsync();

    if (updateCheck is not null)
    {
        await UpdateNotice.PrintIfAvailableAsync(updateCheck, updateCheckMaxWait);
        await updateCheckCts.CancelAsync();
    }

    return exitCode;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine(ConsoleMarkup.Red($"Fatal error: {ex.Message}"));
    return 1;
}