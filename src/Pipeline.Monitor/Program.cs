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

// --reset: delete the database file and exit
if (args.Contains("--reset"))
{
    var resetConfigPath = args.FirstOrDefault(a => !a.StartsWith('-')) ?? MonitorConfig.DefaultConfigPath;
    if (!File.Exists(resetConfigPath))
    {
        // No config — fall back to default database path
        var defaultDb = MonitorConfig.DefaultDatabasePath;
        if (File.Exists(defaultDb))
        {
            File.Delete(defaultDb);
            AnsiConsole.MarkupLine($"[green]Deleted database:[/] {defaultDb}");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No database file found to delete.[/]");
        }
        return 0;
    }

    var resetConfig = MonitorConfig.Load(resetConfigPath);
    var dbPath = Path.GetFullPath(resetConfig.Database);
    if (File.Exists(dbPath))
    {
        File.Delete(dbPath);
        AnsiConsole.MarkupLine($"[green]Deleted database:[/] {dbPath}");
    }
    else
    {
        AnsiConsole.MarkupLine($"[yellow]No database file found at:[/] {dbPath}");
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
