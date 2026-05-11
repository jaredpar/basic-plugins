using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Retries failure collection for builds that previously failed.
/// Uses the same prompt UX as AddCommand — user describes which builds to retry.
/// </summary>
public static class RetryCommand
{
    public static async Task ExecuteAsync(CopilotClient client, MonitorDatabase db, List<AIFunction> pipelineTools)
    {
        var prompt = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Describe which builds to retry:[/] ")
                .PromptStyle("green"));

        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnsiConsole.MarkupLine("[dim]No prompt provided.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[dim]Finding builds...[/]");

        var builds = await BuildPromptHelper.RunBuildPromptAsync(client, pipelineTools, prompt);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds emitted by the agent.[/]");
            return;
        }

        int reset = 0;
        int notFailed = 0;

        foreach (var build in builds)
        {
            if (db.ResetCollectionState(build.AzdoBuildId))
            {
                reset++;
            }
            else
            {
                notFailed++;
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Reset {reset} builds for retry[/] ({notFailed} were not in failed state). Background job will pick them up shortly.");
    }
}
