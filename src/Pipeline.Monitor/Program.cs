using System.Diagnostics;
using Pipeline.Core;
using Pipeline.Monitor;
using Spectre.Console;

const string McpServerName = "monitor-mcp";

// --mcp: register the MCP server with copilot and exit
if (args.Contains("--mcp"))
{
    var configPath2 = args.FirstOrDefault(a => !a.StartsWith('-'));
    var dbPath2 = configPath2 is not null && File.Exists(configPath2)
        ? Path.GetFullPath(MonitorConfig.Load(configPath2).Database)
        : MonitorConfig.DefaultDatabasePath;
    var mcpExeName = OperatingSystem.IsWindows() ? "monitor-mcp.exe" : "monitor.mcp";
    var mcpExePath = Path.Combine(AppContext.BaseDirectory, mcpExeName);
    if (!File.Exists(mcpExePath))
    {
        AnsiConsole.MarkupLine("[red]Could not find Pipeline.Mcp executable.[/]");
        return 1;
    }

    var addArgs = $"mcp add {McpServerName} -- \"{mcpExePath}\" --database \"{dbPath2}\"";

    var result = RunCopilot(addArgs);
    if (result == 0)
        AnsiConsole.MarkupLine($"[green]Registered MCP server '[bold]{McpServerName}[/]' with copilot.[/]");
    else
        AnsiConsole.MarkupLine($"[red]Failed to register MCP server (exit code {result}).[/]");
    return result;
}

// --no-mcp: remove the MCP server from copilot and exit
if (args.Contains("--no-mcp"))
{
    var result = RunCopilot($"mcp remove {McpServerName}");
    if (result == 0)
        AnsiConsole.MarkupLine($"[green]Removed MCP server '[bold]{McpServerName}[/]' from copilot.[/]");
    else
        AnsiConsole.MarkupLine($"[red]Failed to remove MCP server (exit code {result}).[/]");
    return result;
}

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

static int RunCopilot(string arguments)
{
    var psi = new ProcessStartInfo("copilot", arguments)
    {
        UseShellExecute = false,
    };

    using var process = Process.Start(psi);
    process?.WaitForExit();
    return process?.ExitCode ?? 1;
}
