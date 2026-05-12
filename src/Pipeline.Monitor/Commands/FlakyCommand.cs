using System.Diagnostics;
using System.Text.Json;
using Spectre.Console;
using Pipeline.Core;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Interactive command for reviewing flaky test determinations.
/// Shows flaky tests, allows filing issues and spawning fix sessions.
/// </summary>
public static class FlakyCommand
{
    public static void Execute(MonitorClient db)
    {
        while (true)
        {
            var flakyTests = db.GetAllFlakyTests();

            if (flakyTests.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No flaky tests detected yet.[/]");
                AnsiConsole.MarkupLine("[dim]The analysis job will populate this as builds are triaged.[/]");
                return;
            }

            var labels = new List<string>();
            foreach (var ft in flakyTests)
            {
                var issueTag = ft.IssueNumber.HasValue ? $" [#{ft.IssueNumber}]" : "";
                labels.Add($"{ft.TestName} | {ft.Repository} | {ft.OccurrenceCount} occurrences{issueTag}");
            }

            var index = InteractiveMenu(labels, $"Flaky Tests ({flakyTests.Count}) — ↑↓ navigate, Enter select, Esc back:");
            if (index < 0)
                return;

            ShowFlakyDetail(db, flakyTests[index]);
        }
    }

    private static void ShowFlakyDetail(MonitorClient db, MonitorFlakyTestRecord flaky)
    {
        while (true)
        {
            AnsiConsole.Clear();

            var header = new Rule($"[bold]Flaky Test[/]");
            header.Style = Style.Parse("yellow");
            AnsiConsole.Write(header);

            // Info table
            var infoTable = new Table { Border = TableBorder.None, ShowHeaders = false };
            infoTable.AddColumn("Key");
            infoTable.AddColumn("Value");

            infoTable.AddRow("[bold]Test Name[/]", flaky.TestName.EscapeMarkup());
            infoTable.AddRow("[bold]Repository[/]", flaky.Repository.EscapeMarkup());
            infoTable.AddRow("[bold]Occurrences[/]", flaky.OccurrenceCount.ToString());
            infoTable.AddRow("[bold]First Seen[/]", flaky.FirstSeen.EscapeMarkup());
            infoTable.AddRow("[bold]Last Seen[/]", flaky.LastSeen.EscapeMarkup());

            if (flaky.IssueNumber.HasValue)
            {
                infoTable.AddRow("[bold]Issue[/]", $"[link={flaky.IssueUrl}]#{flaky.IssueNumber}[/]");
            }
            else
            {
                infoTable.AddRow("[bold]Issue[/]", "[dim]Not filed[/]");
            }

            AnsiConsole.Write(infoTable);
            AnsiConsole.WriteLine();

            // Show rationale / evidence summary
            if (!string.IsNullOrWhiteSpace(flaky.Rationale))
            {
                var rationaleRule = new Rule("[bold yellow]Rationale & Evidence[/]");
                rationaleRule.Style = Style.Parse("yellow");
                AnsiConsole.Write(rationaleRule);
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Markup(flaky.Rationale.EscapeMarkup()));
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine();
            }

            // Show failure history
            var history = db.GetTestHistoryForName(flaky.TestName, flaky.Repository, limit: 10);
            if (history.Count > 0)
            {
                var histRule = new Rule("[bold]Recent Failures[/]");
                histRule.Style = Style.Parse("dim");
                AnsiConsole.Write(histRule);

                foreach (var h in history)
                {
                    var branch = h.SourceBranch;
                    if (branch.StartsWith("refs/pull/"))
                        branch = $"PR #{branch.Split('/')[2]}";
                    else if (branch.StartsWith("refs/heads/"))
                        branch = branch["refs/heads/".Length..];

                    AnsiConsole.MarkupLine($"  Build {h.AzdoBuildId} | {branch.EscapeMarkup()} | {h.Outcome.EscapeMarkup()} | {(h.FinishTime ?? "").EscapeMarkup()}");

                    if (!string.IsNullOrWhiteSpace(h.ErrorMessage))
                    {
                        var msg = Truncate(h.ErrorMessage, 150);
                        AnsiConsole.MarkupLine($"    [dim]{msg.EscapeMarkup()}[/]");
                    }
                }
                AnsiConsole.WriteLine();
            }

            // Show fix requests
            var fixes = db.GetFixRequestsForTest(flaky.TestName, flaky.Repository);
            if (fixes.Count > 0)
            {
                var fixRule = new Rule("[bold green]Proposed Fixes[/]");
                fixRule.Style = Style.Parse("green");
                AnsiConsole.Write(fixRule);

                foreach (var fix in fixes)
                {
                    AnsiConsole.MarkupLine($"  [bold]Diagnosis:[/] {fix.Diagnosis.EscapeMarkup()}");
                    if (!string.IsNullOrWhiteSpace(fix.ProposedFix))
                    {
                        AnsiConsole.MarkupLine($"  [bold]Fix:[/] {fix.ProposedFix.EscapeMarkup()}");
                    }
                    AnsiConsole.WriteLine();
                }
            }

            // Actions
            var actions = new List<string>();
            if (!flaky.IssueNumber.HasValue)
                actions.Add("File issue");
            if (fixes.Count > 0)
                actions.Add("Attempt fix (spawn copilot)");
            actions.Add("← Back");

            var actionIndex = InteractiveMenu(actions, "Actions:");
            if (actionIndex < 0 || actions[actionIndex] == "← Back")
                return;

            var action = actions[actionIndex];

            if (action == "File issue")
            {
                FileIssue(db, flaky, fixes);
            }
            else if (action.StartsWith("Attempt fix"))
            {
                SpawnFixSession(flaky, fixes);
            }
        }
    }

    private static void FileIssue(MonitorClient db, MonitorFlakyTestRecord flaky, List<MonitorFixRequest> fixes)
    {
        var title = $"Flaky test: {flaky.TestName}";
        var bodyParts = new List<string>
        {
            $"## Flaky Test Detected",
            $"",
            $"**Test:** `{flaky.TestName}`",
            $"**Repository:** {flaky.Repository}",
            $"**Occurrences:** {flaky.OccurrenceCount}",
            $"**First seen:** {flaky.FirstSeen}",
            $"**Last seen:** {flaky.LastSeen}",
        };

        if (!string.IsNullOrWhiteSpace(flaky.Rationale))
        {
            bodyParts.Add("");
            bodyParts.Add("## Rationale & Evidence");
            bodyParts.Add(flaky.Rationale);
        }

        if (fixes.Count > 0)
        {
            bodyParts.Add("");
            bodyParts.Add("## Analysis");
            foreach (var fix in fixes)
            {
                bodyParts.Add($"**Diagnosis:** {fix.Diagnosis}");
                if (!string.IsNullOrWhiteSpace(fix.ProposedFix))
                    bodyParts.Add($"**Proposed fix:** {fix.ProposedFix}");
            }
        }

        var body = string.Join("\n", bodyParts);

        AnsiConsole.MarkupLine($"[bold]Filing issue on {flaky.Repository}...[/]");
        AnsiConsole.MarkupLine($"[dim]Title: {title.EscapeMarkup()}[/]");

        try
        {
            var parts = flaky.Repository.Split('/');
            if (parts.Length != 2)
            {
                AnsiConsole.MarkupLine("[red]Invalid repository format.[/]");
                return;
            }

            var psi = new ProcessStartInfo("gh", [
                "issue", "create",
                "--repo", flaky.Repository,
                "--title", title,
                "--body", body,
                "--label", "flaky-test",
            ])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                AnsiConsole.MarkupLine("[red]Failed to start gh CLI.[/]");
                return;
            }

            var output = proc.StandardOutput.ReadToEnd();
            var error = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode == 0)
            {
                var issueUrl = output.Trim();
                // Extract issue number from URL (e.g., https://github.com/owner/repo/issues/123)
                var issueNumberStr = issueUrl.Split('/').LastOrDefault();
                if (int.TryParse(issueNumberStr, out var issueNumber))
                {
                    db.UpdateFlakyTestIssue(flaky.Id, issueNumber, issueUrl);
                    AnsiConsole.MarkupLine($"[green]Issue filed: {issueUrl.EscapeMarkup()}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Issue created but couldn't parse number: {issueUrl.EscapeMarkup()}[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]gh failed: {error.EscapeMarkup()}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error filing issue: {ex.Message.EscapeMarkup()}[/]");
        }

        AnsiConsole.MarkupLine("[dim]Press Esc to continue...[/]");
        WaitForEsc();
    }

    private static void SpawnFixSession(MonitorFlakyTestRecord flaky, List<MonitorFixRequest> fixes)
    {
        var diagnosis = fixes.FirstOrDefault()?.Diagnosis ?? "Unknown";
        var proposedFix = fixes.FirstOrDefault()?.ProposedFix ?? "";
        var rationale = flaky.Rationale ?? "";

        var prompt = $"""
            Fix the flaky test: {flaky.TestName}
            Repository: {flaky.Repository}
            
            Diagnosis: {diagnosis}
            
            Rationale: {rationale}
            
            Proposed fix: {proposedFix}
            
            Please investigate and implement the fix.
            """;

        AnsiConsole.MarkupLine("[bold]Spawning Copilot fix session...[/]");
        AnsiConsole.MarkupLine($"[dim]{prompt.EscapeMarkup()}[/]");

        try
        {
            // Launch copilot CLI in a new terminal
            var psi = new ProcessStartInfo("copilot", ["-p", prompt])
            {
                UseShellExecute = false,
            };

            Process.Start(psi);
            AnsiConsole.MarkupLine("[green]Copilot session launched.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to launch copilot: {ex.Message.EscapeMarkup()}[/]");
        }

        AnsiConsole.MarkupLine("[dim]Press Esc to continue...[/]");
        WaitForEsc();
    }

    private static void WaitForEsc()
    {
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key is ConsoleKey.Escape or ConsoleKey.Q)
                break;
        }
    }

    private static int InteractiveMenu(List<string> items, string title)
    {
        int selected = 0;
        int pageSize = Math.Min(20, Console.WindowHeight - 4);

        Console.CursorVisible = false;
        try
        {
            while (true)
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine($"[bold]{title.EscapeMarkup()}[/]");
                AnsiConsole.WriteLine();

                int start = Math.Max(0, selected - pageSize / 2);
                int end = Math.Min(items.Count, start + pageSize);
                if (end - start < pageSize)
                    start = Math.Max(0, end - pageSize);

                if (start > 0)
                    AnsiConsole.MarkupLine("[dim]  ↑ more[/]");

                for (int i = start; i < end; i++)
                {
                    if (i == selected)
                        AnsiConsole.MarkupLine($"[blue]> {items[i].EscapeMarkup()}[/]");
                    else
                        AnsiConsole.MarkupLine($"  {items[i].EscapeMarkup()}");
                }

                if (end < items.Count)
                    AnsiConsole.MarkupLine("[dim]  ↓ more[/]");

                var key = Console.ReadKey(true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = Math.Max(0, selected - 1);
                        break;
                    case ConsoleKey.DownArrow:
                        selected = Math.Min(items.Count - 1, selected + 1);
                        break;
                    case ConsoleKey.Enter:
                        return selected;
                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        return -1;
                }
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength), "…");
    }
}


