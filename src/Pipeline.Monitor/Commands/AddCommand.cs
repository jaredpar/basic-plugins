using GitHub.Copilot.SDK;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Adds builds to the database using a natural language prompt.
/// Test failures and Helix data are collected by the background FailureCollectionJob.
/// </summary>
public static class AddCommand
{
    public static async Task ExecuteAsync(CopilotClient client, MonitorDatabase db)
    {
        var prompt = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Describe what builds to add:[/] ")
                .PromptStyle("green"));

        if (string.IsNullOrWhiteSpace(prompt))
        {
            AnsiConsole.MarkupLine("[dim]No prompt provided.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[dim]Fetching builds...[/]");

        var builds = await BuildPromptHelper.RunBuildPromptAsync(client, prompt);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds emitted by the agent.[/]");
            return;
        }

        int imported = 0;
        int skipped = 0;

        foreach (var build in builds)
        {
            if (db.HasBuild(build.AzdoBuildId))
            {
                skipped++;
                continue;
            }

            db.InsertBuild(
                build.AzdoBuildId,
                build.Repository,
                build.BuildNumber,
                build.SourceBranch,
                build.DefinitionName,
                build.Status,
                build.Result,
                null,
                hasTestFailures: false);

            imported++;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Added {imported} builds[/] ({skipped} already existed). Failure data will be collected in the background.");
    }
}
