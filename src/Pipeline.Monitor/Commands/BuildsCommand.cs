using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Displays builds from the SQLite database in a formatted table.
/// </summary>
public static class BuildsCommand
{
    public static void Execute(MonitorDatabase db)
    {
        var builds = db.GetRecentBuilds(50);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No builds in the database yet.[/]");
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn("Build ID");
        table.AddColumn("Repository");
        table.AddColumn("Branch");
        table.AddColumn("Definition");
        table.AddColumn("Result");
        table.AddColumn("Failures");
        table.AddColumn("Finished");

        foreach (var build in builds)
        {
            var resultMarkup = build.Result switch
            {
                "succeeded" => "[green]succeeded[/]",
                "failed" => "[red]failed[/]",
                "partiallySucceeded" => "[yellow]partiallySucceeded[/]",
                "canceled" => "[dim]canceled[/]",
                _ => build.Result ?? "[dim]—[/]",
            };

            var failureMarkup = build.HasTestFailures
                ? $"[red]{build.TestFailureCount}[/]"
                : "[dim]0[/]";

            // Shorten branch for display
            var branch = build.SourceBranch;
            if (branch.StartsWith("refs/pull/"))
                branch = $"PR #{branch.Split('/')[2]}";
            else if (branch.StartsWith("refs/heads/"))
                branch = branch["refs/heads/".Length..];

            table.AddRow(
                build.AzdoBuildId.ToString(),
                build.Repository,
                branch.EscapeMarkup(),
                build.DefinitionName.EscapeMarkup(),
                resultMarkup,
                failureMarkup,
                build.FinishTime ?? "[dim]—[/]");
        }

        AnsiConsole.Write(table);
    }
}
