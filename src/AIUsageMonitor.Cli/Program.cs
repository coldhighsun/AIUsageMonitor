using System.CommandLine;
using AIUsageMonitor.Cli;
using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Services;
using AIUsageMonitor.UpdateCheck;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddClaudeUsageCore();
    builder.Services.AddHttpClient<IUpdateChecker, UpdateChecker>();
    var host = builder.Build();

    var dataService = host.Services.GetRequiredService<DataService>();
    var updateChecker = host.Services.GetRequiredService<IUpdateChecker>();

    var rootCommand = new RootCommand("aimon - AI Usage Monitor");

    rootCommand.Subcommands.Add(TodayCommand.Create(dataService));
    rootCommand.Subcommands.Add(WeekCommand.Create(dataService));
    rootCommand.Subcommands.Add(MonthCommand.Create(dataService));
    rootCommand.Subcommands.Add(ModelsCommand.Create(dataService));
    rootCommand.Subcommands.Add(SessionsCommand.Create(dataService));
    rootCommand.Subcommands.Add(HoursCommand.Create(dataService));
    rootCommand.Subcommands.Add(ExportCommand.Create(dataService));
    var watchCommand = WatchCommand.Create(dataService, updateChecker);
    rootCommand.Subcommands.Add(watchCommand);

    var parseResult = rootCommand.Parse(args);
    var isWatch = parseResult.CommandResult.Command == watchCommand;

    if (parseResult.Errors.Count == 0 && !Console.IsOutputRedirected)
    {
        AnsiConsole.Clear();
    }

    var exitCode = await parseResult.InvokeAsync();

    // watch runs its own update check up front (it's a long-running loop, so the notice needs to
    // show while it's running, not after it exits) - every other command checks once here, after
    // its own output, so the check never delays the command's actual result.
    if (!isWatch)
    {
        await UpdateNotice.PrintIfAvailableAsync(updateChecker);
    }

    return exitCode;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Fatal error: {ex.Message}[/]");
    return 1;
}