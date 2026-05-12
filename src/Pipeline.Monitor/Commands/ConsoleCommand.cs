using System.Diagnostics;
using Pipeline.Core;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Launches the copilot CLI in the current console with an isolated COPILOT_HOME
/// that has the monitor MCP server and skills pre-configured.
/// </summary>
public static class ConsoleCommand
{
    private static readonly string s_copilotHome = Path.Combine(PipelineUtils.AppDataDirectory, "copilot-home");

    public static async Task ExecuteAsync(string databasePath, bool clean)
    {
        if (clean && Directory.Exists(s_copilotHome))
        {
            AnsiConsole.MarkupLine("[yellow]Cleaning existing copilot home...[/]");
            Directory.Delete(s_copilotHome, recursive: true);
        }

        var needsSetup = !Directory.Exists(s_copilotHome);
        if (needsSetup)
        {
            Directory.CreateDirectory(s_copilotHome);
            AnsiConsole.MarkupLine($"[dim]Created COPILOT_HOME: {s_copilotHome.EscapeMarkup()}[/]");

            // Install the MCP server
            var mcpExeName = OperatingSystem.IsWindows() ? "monitor-mcp.exe" : "monitor-mcp";
            var mcpExePath = Path.Combine(AppContext.BaseDirectory, mcpExeName);
            if (!File.Exists(mcpExePath))
            {
                AnsiConsole.MarkupLine("[red]Could not find monitor-mcp executable.[/]");
                return;
            }

            AnsiConsole.MarkupLine("[dim]Registering MCP server...[/]");
            var mcpArgs = $"mcp add monitor-mcp -- \"{mcpExePath}\" --database \"{databasePath}\"";
            var mcpResult = RunCopilot(mcpArgs);
            if (mcpResult != 0)
            {
                AnsiConsole.MarkupLine($"[red]Failed to register MCP server (exit code {mcpResult}).[/]");
                return;
            }
            AnsiConsole.MarkupLine("[green]MCP server registered.[/]");

        // Launch copilot interactively in this console
        AnsiConsole.MarkupLine("[bold blue]Launching copilot...[/]");
        AnsiConsole.WriteLine();

        var copilotExe = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
        var psi = new ProcessStartInfo(copilotExe)
        {
            UseShellExecute = false,
        };
        psi.Environment["COPILOT_HOME"] = s_copilotHome;

        using var process = Process.Start(psi);
        if (process is null)
        {
            AnsiConsole.MarkupLine("[red]Failed to start copilot process.[/]");
            return;
        }

        await process.WaitForExitAsync();
    }

    private static int RunCopilot(string arguments)
    {
        var copilotExe = OperatingSystem.IsWindows() ? "copilot.exe" : "copilot";
        var psi = new ProcessStartInfo(copilotExe, arguments)
        {
            UseShellExecute = false,
        };
        psi.Environment["COPILOT_HOME"] = s_copilotHome;

        using var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }
}
