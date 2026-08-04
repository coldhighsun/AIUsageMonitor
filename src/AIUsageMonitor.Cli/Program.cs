using System.CommandLine;
using AIUsageMonitor.Cli.Commands;
using AIUsageMonitor.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spectre.Console;

var logPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "aimon", "logs", "aimon-.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("aimon started with args: {Args}", string.Join(" ", args));

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
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
    Log.Fatal(ex, "aimon terminated unexpectedly");
    AnsiConsole.MarkupLine($"[red]Fatal error: {ex.Message}[/]");
    AnsiConsole.MarkupLine($"[grey]See logs: {logPath}[/]");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
