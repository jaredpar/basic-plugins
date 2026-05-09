using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Displays builds from the SQLite database with collection status.
/// Allows selecting a build to see detailed failure information.
/// </summary>
public static class BuildsCommand
{
    public static void Execute(MonitorDatabase db)
    {
        while (true)
        {
            var builds = db.GetRecentBuilds(50);

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No builds in the database yet.[/]");
                return;
            }

            var choices = new List<string>();
            var buildMap = new Dictionary<string, BuildRecord>();

            foreach (var build in builds)
            {
                var branch = FormatBranch(build.SourceBranch);
                var result = build.Result ?? "—";
                var label = $"{build.AzdoBuildId} | {build.Repository} | {branch} | {result} | {build.TestFailureCount} failures"
                    .EscapeMarkup();
                choices.Add(label);
                buildMap[label] = build;
            }

            var backLabel = "← Back";
            choices.Add(backLabel);

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Select a build to view details:[/]")
                    .PageSize(20)
                    .AddChoices(choices));

            if (selection == backLabel)
                return;

            var selected = buildMap[selection];
            ShowBuildDetail(db, selected);
        }
    }

    private static void ShowBuildDetail(MonitorDatabase db, BuildRecord build)
    {
        AnsiConsole.Clear();
        var branch = FormatBranch(build.SourceBranch);

        // Header
        var header = new Rule($"[bold]Build {build.AzdoBuildId}[/]");
        header.Style = Style.Parse("blue");
        AnsiConsole.Write(header);

        // Build info table
        var infoTable = new Table { Border = TableBorder.None, ShowHeaders = false };
        infoTable.AddColumn("Key");
        infoTable.AddColumn("Value");

        infoTable.AddRow("[bold]Repository[/]", build.Repository.EscapeMarkup());
        infoTable.AddRow("[bold]Definition[/]", build.DefinitionName.EscapeMarkup());
        infoTable.AddRow("[bold]Build #[/]", build.BuildNumber.EscapeMarkup());
        infoTable.AddRow("[bold]Branch[/]", branch.EscapeMarkup());
        infoTable.AddRow("[bold]Result[/]", FormatResult(build.Result));
        infoTable.AddRow("[bold]Finished[/]", build.FinishTime?.EscapeMarkup() ?? "[dim]—[/]");
        infoTable.AddRow("[bold]AzDO Collection[/]", FormatState(build.AzdoFailureState));
        infoTable.AddRow("[bold]Helix Collection[/]", FormatState(build.HelixFailureState));

        // PR link
        if (build.SourceBranch.StartsWith("refs/pull/"))
        {
            var prNumber = build.SourceBranch.Split('/')[2];
            var prUrl = $"https://github.com/{build.Repository}/pull/{prNumber}";
            infoTable.AddRow("[bold]PR Link[/]", $"[link={prUrl}]{prUrl}[/]");
        }

        // AzDO build link
        var azdoUrl = $"https://dev.azure.com/dnceng-public/public/_build/results?buildId={build.AzdoBuildId}";
        infoTable.AddRow("[bold]AzDO Link[/]", $"[link={azdoUrl}]{azdoUrl}[/]");

        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Test failures
        var testFailures = db.GetTestFailuresForBuild(build.AzdoBuildId);
        if (testFailures.Count > 0)
        {
            var failureRule = new Rule($"[bold red]Test Failures ({testFailures.Count})[/]");
            failureRule.Style = Style.Parse("red");
            AnsiConsole.Write(failureRule);

            foreach (var failure in testFailures)
            {
                AnsiConsole.MarkupLine($"  [red]✗[/] [bold]{failure.TestName.EscapeMarkup()}[/] [dim]({failure.Outcome.EscapeMarkup()})[/]");

                if (!string.IsNullOrWhiteSpace(failure.ErrorMessage))
                {
                    var msg = Truncate(failure.ErrorMessage, 200);
                    AnsiConsole.MarkupLine($"    [dim]{msg.EscapeMarkup()}[/]");
                }

                if (!string.IsNullOrWhiteSpace(failure.StackTrace))
                {
                    var trace = Truncate(failure.StackTrace, 300);
                    AnsiConsole.MarkupLine($"    [dim italic]{trace.EscapeMarkup()}[/]");
                }

                AnsiConsole.WriteLine();
            }
        }
        else if (build.AzdoFailureState == "collected")
        {
            AnsiConsole.MarkupLine("[green]No test failures recorded.[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Test failure data not yet collected.[/]");
            AnsiConsole.WriteLine();
        }

        // Helix work items
        var helixItems = db.GetHelixWorkItemsForBuild(build.AzdoBuildId);
        if (helixItems.Count > 0)
        {
            var helixRule = new Rule($"[bold yellow]Failed Helix Work Items ({helixItems.Count})[/]");
            helixRule.Style = Style.Parse("yellow");
            AnsiConsole.Write(helixRule);

            var helixTable = new Table();
            helixTable.Border(TableBorder.Simple);
            helixTable.AddColumn("Name");
            helixTable.AddColumn("Exit");
            helixTable.AddColumn("Queue");
            helixTable.AddColumn("Machine");
            helixTable.AddColumn("Console");

            foreach (var item in helixItems)
            {
                helixTable.AddRow(
                    Truncate(item.FriendlyName, 60).EscapeMarkup(),
                    item.ExitCode.ToString(),
                    item.QueueName.EscapeMarkup(),
                    item.MachineName.EscapeMarkup(),
                    $"[link={item.ConsoleUri}]logs[/]");
            }

            AnsiConsole.Write(helixTable);
            AnsiConsole.WriteLine();
        }
        else if (build.HelixFailureState == "collected")
        {
            AnsiConsole.MarkupLine("[green]No failed Helix work items.[/]");
            AnsiConsole.WriteLine();
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Helix data not yet collected.[/]");
            AnsiConsole.WriteLine();
        }

        // Wait for user before returning to list
        AnsiConsole.MarkupLine("[dim]Press q or Esc to return to build list...[/]");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                break;
        }
    }

    private static string FormatBranch(string branch)
    {
        if (branch.StartsWith("refs/pull/"))
            return $"PR #{branch.Split('/')[2]}";
        if (branch.StartsWith("refs/heads/"))
            return branch["refs/heads/".Length..];
        return branch;
    }

    private static string FormatResult(string? result) => result switch
    {
        "succeeded" => "[green]succeeded[/]",
        "failed" => "[red]failed[/]",
        "partiallySucceeded" => "[yellow]partial[/]",
        "canceled" => "[dim]canceled[/]",
        _ => result?.EscapeMarkup() ?? "[dim]—[/]",
    };

    private static string FormatState(string state) => state switch
    {
        "collected" => "[green]✓ collected[/]",
        "pending" => "[yellow]… pending[/]",
        "failed" => "[red]✗ failed[/]",
        _ => state.EscapeMarkup(),
    };

    private static string Truncate(string text, int maxLength)
    {
        // Collapse newlines for inline display
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength), "…");
    }
}
