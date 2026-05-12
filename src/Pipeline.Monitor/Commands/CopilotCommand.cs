using System.Diagnostics;
using Pipeline.Core;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Manages the monitor MCP server registration with the copilot CLI.
/// Subcommands: install, uninstall, info.
/// </summary>
public static class CopilotCommand
{
    private const string McpServerName = "monitor-mcp";

    public static void Execute(string subcommand, string databasePath)
    {
        switch (subcommand)
        {
            case "install":
                Install(databasePath);
                break;
            case "uninstall":
                Uninstall();
                break;
            case "info":
                Info(databasePath);
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown subcommand:[/] {subcommand}");
                AnsiConsole.MarkupLine("Usage: [bold]copilot install|uninstall|info[/]");
                break;
        }
    }

    private static void Install(string databasePath)
    {
        var mcpExeName = OperatingSystem.IsWindows() ? "monitor-mcp.exe" : "monitor-mcp";
        var mcpExePath = Path.Combine(AppContext.BaseDirectory, mcpExeName);
        if (!File.Exists(mcpExePath))
        {
            AnsiConsole.MarkupLine("[red]Could not find monitor-mcp executable.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[dim]Registering MCP server...[/]");
        var args = $"mcp add {McpServerName} -- \"{mcpExePath}\" --database \"{databasePath}\"";
        var result = RunCopilot(args);
        if (result == 0)
            AnsiConsole.MarkupLine($"[green]Registered MCP server '[bold]{McpServerName}[/]' with copilot.[/]");
        else
            AnsiConsole.MarkupLine($"[red]Failed to register MCP server (exit code {result}).[/]");
    }

    private static void Uninstall()
    {
        AnsiConsole.MarkupLine("[dim]Removing MCP server...[/]");
        var result = RunCopilot($"mcp remove {McpServerName}");
        if (result == 0)
            AnsiConsole.MarkupLine($"[green]Removed MCP server '[bold]{McpServerName}[/]' from copilot.[/]");
        else
            AnsiConsole.MarkupLine($"[red]Failed to remove MCP server (exit code {result}).[/]");
    }

    private static void Info(string databasePath)
    {
        var mcpExeName = OperatingSystem.IsWindows() ? "monitor-mcp.exe" : "monitor-mcp";
        var mcpExePath = Path.Combine(AppContext.BaseDirectory, mcpExeName);

        var table = new Table { Border = TableBorder.None, ShowHeaders = false };
        table.AddColumn("Key");
        table.AddColumn("Value");

        table.AddRow("[bold]MCP Server Name[/]", McpServerName);
        table.AddRow("[bold]MCP Executable[/]", mcpExePath.EscapeMarkup());
        table.AddRow("[bold]MCP Exists[/]", File.Exists(mcpExePath) ? "[green]yes[/]" : "[red]no[/]");
        table.AddRow("[bold]Database Path[/]", databasePath.EscapeMarkup());
        table.AddRow("[bold]Skills Directory[/]", SessionConfigHelper.SkillsDirectory.EscapeMarkup());
        table.AddRow("[bold]Skills Exist[/]", Directory.Exists(SessionConfigHelper.SkillsDirectory) ? "[green]yes[/]" : "[red]no[/]");

        AnsiConsole.Write(table);
    }

    private static int RunCopilot(string arguments)
    {
        var copilotExe = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
        var psi = new ProcessStartInfo(copilotExe, arguments)
        {
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }
}
