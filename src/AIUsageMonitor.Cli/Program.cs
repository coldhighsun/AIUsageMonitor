using System.CommandLine;
using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddClaudeUsageCore();
    var host = builder.Build();

    var dataService = host.Services.GetRequiredService<DataService>();

    var rootCommand = new RootCommand("aimon - AI Usage Monitor");

    rootCommand.Subcommands.Add(TodayCommand.Create(dataService));
    rootCommand.Subcommands.Add(WeekCommand.Create(dataService));
    rootCommand.Subcommands.Add(MonthCommand.Create(dataService));
    rootCommand.Subcommands.Add(ModelsCommand.Create(dataService));
    rootCommand.Subcommands.Add(SessionsCommand.Create(dataService));
    rootCommand.Subcommands.Add(HoursCommand.Create(dataService));
    rootCommand.Subcommands.Add(ExportCommand.Create(dataService));
    rootCommand.Subcommands.Add(WatchCommand.Create(dataService));

    return await rootCommand.Parse(args).InvokeAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Fatal error: {ex.Message}[/]");
    return 1;
}