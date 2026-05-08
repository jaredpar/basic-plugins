using Pipeline.Monitor;
using Spectre.Console;

// --init: create app data directory with default config and exit
if (args.Contains("--init"))
{
    try
    {
        var created = MonitorConfig.Initialize();
        AnsiConsole.MarkupLine($"[green]Created config at:[/] {created}");
        AnsiConsole.MarkupLine($"[green]App data directory:[/] {MonitorConfig.AppDataDirectory}");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Default configuration monitors [bold]dotnet/roslyn[/] (PR + CI builds).");
        AnsiConsole.MarkupLine("Edit the config file to add more pipelines or change settings.");
    }
    catch (InvalidOperationException ex)
    {
        AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
        return 1;
    }
    return 0;
}

// Resolve config path
var configPath = args.FirstOrDefault(a => !a.StartsWith('-')) ?? MonitorConfig.DefaultConfigPath;

if (!File.Exists(configPath))
{
    AnsiConsole.MarkupLine($"[red]Configuration file not found:[/] {configPath}");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("Run with [bold]--init[/] to create a default configuration:");
    AnsiConsole.MarkupLine("  monitor --init");
    return 1;
}

var config = MonitorConfig.Load(configPath);

await using var app = new MonitorApp(config);
await app.RunAsync();
return 0;
