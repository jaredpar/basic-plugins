using System.Text.Json;
using System.Text.Json.Serialization;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Displays builds from the SQLite database with collection status.
/// Allows selecting a build to see detailed failure information,
/// drill into Helix work item logs, and view flaky analysis chat logs.
/// </summary>
public static class BuildsCommand
{
    public static async Task ExecuteAsync(MonitorDatabase db)
    {
        while (true)
        {
            var builds = db.GetRecentBuilds(50, failedOnly: true);

            if (builds.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No failed builds in the database yet.[/]");
                return;
            }

            var labels = new List<string>();
            foreach (var build in builds)
            {
                var branch = FormatBranch(build.SourceBranch);
                var result = build.Result ?? "—";
                var testInfo = build.TestFailureCount > 0 ? $"{build.TestFailureCount} tests" : "";
                var helixInfo = build.HelixFailureCount > 0 ? $"{build.HelixFailureCount} helix" : "";
                var failureParts = new[] { testInfo, helixInfo }.Where(s => s.Length > 0);
                var failureSummary = failureParts.Any() ? string.Join(", ", failureParts) : "no failures collected";
                labels.Add($"{build.AzdoBuildId} | {build.Repository} | {branch} | {result} | {failureSummary}");
            }

            var index = InteractiveMenu(labels, "Select a build (↑↓ navigate, Enter select, Esc back):");
            if (index < 0)
                return;

            await ShowBuildDetailAsync(db, builds[index]);
        }
    }

    /// <summary>
    /// Renders a simple interactive menu with arrow key navigation.
    /// Returns the selected index, or -1 if Escape/q was pressed.
    /// </summary>
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

    private static async Task ShowBuildDetailAsync(MonitorDatabase db, BuildRecord build)
    {
        var helixItems = db.GetHelixWorkItemsForBuild(build.AzdoBuildId);
        var triageDetail = db.GetTriageDetailForBuild(build.AzdoBuildId);

        while (true)
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

            if (build.SourceBranch.StartsWith("refs/pull/"))
            {
                var prNumber = build.SourceBranch.Split('/')[2];
                var prUrl = $"https://github.com/{build.Repository}/pull/{prNumber}";
                infoTable.AddRow("[bold]PR Link[/]", $"[link={prUrl}]{prUrl}[/]");
            }

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
            else if (build.AzdoFailureState == "failed")
            {
                AnsiConsole.MarkupLine("[red]AzDO test failure collection gave up after repeated errors.[/]");
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Test failure data not yet collected.[/]");
                AnsiConsole.WriteLine();
            }

            // Timeline — failed jobs and issues
            var timeline = db.GetTimelineData(build.AzdoBuildId);
            if (timeline is not null && (timeline.FailedJobs.Count > 0 || timeline.Issues.Count > 0))
            {
                var timelineRule = new Rule($"[bold]Timeline ({timeline.FailedJobs.Count} failed job(s), {timeline.Issues.Count} issue(s))[/]");
                timelineRule.Style = Style.Parse("yellow");
                AnsiConsole.Write(timelineRule);

                if (timeline.FailedJobs.Count > 0)
                {
                    var jobTable = new Table();
                    jobTable.Border(TableBorder.Simple);
                    jobTable.AddColumn("Job");
                    jobTable.AddColumn("Result");
                    jobTable.AddColumn("Worker");

                    foreach (var job in timeline.FailedJobs)
                    {
                        var resultMarkup = job.Result switch
                        {
                            "failed" => "[red]failed[/]",
                            "canceled" => "[dim]canceled[/]",
                            _ => job.Result.EscapeMarkup(),
                        };
                        jobTable.AddRow(
                            job.Name.EscapeMarkup(),
                            resultMarkup,
                            job.WorkerName?.EscapeMarkup() ?? "[dim]—[/]");
                    }

                    AnsiConsole.Write(jobTable);
                    AnsiConsole.WriteLine();
                }

                var errors = timeline.Issues.Where(i => i.Type == "error").ToList();
                if (errors.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[red]Errors ({errors.Count}):[/]");
                    foreach (var issue in errors)
                    {
                        var msg = Truncate(issue.Message, 200);
                        AnsiConsole.MarkupLine($"  [red]✗[/] {msg.EscapeMarkup()}");
                    }
                    AnsiConsole.WriteLine();
                }

                var warnings = timeline.Issues.Where(i => i.Type == "warning").ToList();
                if (warnings.Count > 0)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warnings ({warnings.Count}):[/]");
                    foreach (var issue in warnings.Take(10))
                    {
                        var msg = Truncate(issue.Message, 200);
                        AnsiConsole.MarkupLine($"  [yellow]![/] {msg.EscapeMarkup()}");
                    }
                    if (warnings.Count > 10)
                        AnsiConsole.MarkupLine($"  [dim]... and {warnings.Count - 10} more[/]");
                    AnsiConsole.WriteLine();
                }
            }

            // Helix work items summary table
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
            else if (build.HelixFailureState == "failed")
            {
                AnsiConsole.MarkupLine("[red]Helix work item collection gave up after repeated errors.[/]");
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Helix data not yet collected.[/]");
                AnsiConsole.WriteLine();
            }

            // Hotkey bar
            bool canRetry = build.AzdoFailureState == "failed" || build.HelixFailureState == "failed";
            var hotkeys = new List<string>();
            if (helixItems.Count > 0)
                hotkeys.Add("[bold blue]H[/] Helix Logs");
            if (triageDetail is not null)
                hotkeys.Add("[bold blue]F[/] Flaky Analysis");
            if (canRetry)
                hotkeys.Add("[bold blue]R[/] Retry Collection");
            hotkeys.Add("[bold blue]Esc[/] Back");

            AnsiConsole.MarkupLine(string.Join("  │  ", hotkeys));

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.H when helixItems.Count > 0:
                    await ShowHelixDrillDownAsync(helixItems);
                    break;
                case ConsoleKey.F when triageDetail is not null:
                    ShowFlakyAnalysis(triageDetail);
                    break;
                case ConsoleKey.R when canRetry:
                    if (db.ResetCollectionState(build.AzdoBuildId))
                    {
                        AnsiConsole.MarkupLine("[green]Collection state reset — will retry on next poll.[/]");
                        // Refresh the build record to reflect new state
                        build = db.GetBuildByAzdoId(build.AzdoBuildId) ?? build;
                        helixItems = db.GetHelixWorkItemsForBuild(build.AzdoBuildId);
                        triageDetail = db.GetTriageDetailForBuild(build.AzdoBuildId);
                    }
                    break;
                case ConsoleKey.Escape:
                case ConsoleKey.Q:
                    return;
            }
        }
    }

    // --- Helix drill-down ---

    private static async Task ShowHelixDrillDownAsync(List<HelixWorkItemRecord> helixItems)
    {
        while (true)
        {
            var labels = helixItems.Select(item =>
                $"{Truncate(item.FriendlyName, 60)} | exit {item.ExitCode} | {item.QueueName}")
                .ToList();

            var index = InteractiveMenu(labels, "Select a Helix work item to view console log (Esc back):");
            if (index < 0)
                return;

            var selected = helixItems[index];
            await ShowHelixConsoleAsync(selected);
        }
    }

    private static async Task ShowHelixConsoleAsync(HelixWorkItemRecord item)
    {
        AnsiConsole.Clear();

        var header = new Rule($"[bold]Helix: {item.FriendlyName.EscapeMarkup()}[/]");
        header.Style = Style.Parse("yellow");
        AnsiConsole.Write(header);

        var infoTable = new Table { Border = TableBorder.None, ShowHeaders = false };
        infoTable.AddColumn("Key");
        infoTable.AddColumn("Value");
        infoTable.AddRow("[bold]Exit Code[/]", item.ExitCode.ToString());
        infoTable.AddRow("[bold]Queue[/]", item.QueueName.EscapeMarkup());
        infoTable.AddRow("[bold]Machine[/]", item.MachineName.EscapeMarkup());
        infoTable.AddRow("[bold]Status[/]", item.Status.EscapeMarkup());
        infoTable.AddRow("[bold]Console Log[/]", $"[link={item.ConsoleUri}]{item.ConsoleUri.EscapeMarkup()}[/]");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Show stored summary
        if (item.ConsoleSummary is not null)
        {
            var summaryRule = new Rule("[bold]Error Summary (extracted)[/]");
            summaryRule.Style = Style.Parse("red");
            AnsiConsole.Write(summaryRule);
            AnsiConsole.WriteLine(item.ConsoleSummary);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No stored console summary available.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press [bold]F[/] to fetch full log, [bold]D[/] to download log, any other key to return...[/]");
        var key = Console.ReadKey(true);

        if (key.Key == ConsoleKey.F)
        {
            await FetchAndShowFullLogAsync(item);
        }
        else if (key.Key == ConsoleKey.D)
        {
            await DownloadLogAsync(item);
        }
    }

    private static async Task FetchAndShowFullLogAsync(HelixWorkItemRecord item)
    {
        AnsiConsole.WriteLine();
        try
        {
            string consoleText = "";
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Fetching full console log...", async ctx =>
                {
                    using var httpClient = new HttpClient();
                    consoleText = await httpClient.GetStringAsync(item.ConsoleUri);
                });

            var logRule = new Rule("[bold]Full Console Output[/]");
            logRule.Style = Style.Parse("dim");
            AnsiConsole.Write(logRule);

            var lines = consoleText.Split('\n');
            var consoleWidth = Console.WindowWidth;
            if (lines.Length > 200)
            {
                AnsiConsole.MarkupLine($"[dim]... ({lines.Length - 200} lines omitted, showing last 200) ...[/]");
                AnsiConsole.WriteLine();
            }

            foreach (var line in lines.TakeLast(200))
            {
                var trimmed = line.Length > consoleWidth ? line[..consoleWidth] : line;
                AnsiConsole.WriteLine(trimmed);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to fetch console log: {ex.Message.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return...[/]");
        Console.ReadKey(true);
    }

    private static async Task DownloadLogAsync(HelixWorkItemRecord item)
    {
        AnsiConsole.WriteLine();
        try
        {
            var downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloadsDir);

            var safeName = string.Join("_", item.FriendlyName.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"helix-{safeName}-{item.WorkItemId}.log";
            var filePath = Path.Combine(downloadsDir, fileName);

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Downloading console log...", async ctx =>
                {
                    using var httpClient = new HttpClient();
                    var consoleText = await httpClient.GetStringAsync(item.ConsoleUri);
                    await File.WriteAllTextAsync(filePath, consoleText);
                });

            AnsiConsole.MarkupLine($"[green]Downloaded to:[/] {filePath.EscapeMarkup()}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to download console log: {ex.Message.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return...[/]");
        Console.ReadKey(true);
    }

    // --- Flaky analysis drill-down ---

    private static void ShowFlakyAnalysis(TriageDetail detail)
    {
        AnsiConsole.Clear();

        var header = new Rule("[bold]Flaky Analysis[/]");
        header.Style = Style.Parse("green");
        AnsiConsole.Write(header);

        var infoTable = new Table { Border = TableBorder.None, ShowHeaders = false };
        infoTable.AddColumn("Key");
        infoTable.AddColumn("Value");
        infoTable.AddRow("[bold]Status[/]", FormatTriageStatus(detail.Status));
        infoTable.AddRow("[bold]Created[/]", detail.CreatedAt.EscapeMarkup());
        infoTable.AddRow("[bold]Completed[/]", detail.CompletedAt?.EscapeMarkup() ?? "[dim]—[/]");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Show findings summary
        if (detail.ResultJson is not null && detail.Status == "completed")
        {
            try
            {
                var findings = JsonSerializer.Deserialize<List<FlakyFindingView>>(detail.ResultJson);
                if (findings is { Count: > 0 })
                {
                    var findingsRule = new Rule($"[bold]Determinations ({findings.Count})[/]");
                    findingsRule.Style = Style.Parse("yellow");
                    AnsiConsole.Write(findingsRule);

                    foreach (var f in findings)
                    {
                        var icon = f.IsFlaky ? "[red]⚠[/]" : "[green]✓[/]";
                        var flakyLabel = f.IsFlaky ? "[red]FLAKY[/]" : "[green]Not flaky[/]";
                        AnsiConsole.MarkupLine($"  {icon} [bold]{f.TestName.EscapeMarkup()}[/] — {flakyLabel} [dim]({f.Confidence.EscapeMarkup()})[/]");
                        AnsiConsole.MarkupLine($"    {f.Diagnosis.EscapeMarkup()}");

                        if (!string.IsNullOrWhiteSpace(f.ProposedFix))
                        {
                            AnsiConsole.MarkupLine($"    [blue]Fix:[/] {f.ProposedFix.EscapeMarkup()}");
                        }

                        AnsiConsole.WriteLine();
                    }
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not parse findings JSON.[/]");
            }
        }
        else if (detail.Status == "failed")
        {
            AnsiConsole.MarkupLine($"[red]Triage failed:[/] {(detail.ResultJson ?? "unknown error").EscapeMarkup()}");
            AnsiConsole.WriteLine();
        }

        // Show the full AI chat log
        if (detail.ChatLog is not null)
        {
            var chatRule = new Rule("[bold]AI Chat Log[/]");
            chatRule.Style = Style.Parse("blue");
            AnsiConsole.Write(chatRule);
            AnsiConsole.WriteLine();

            try
            {
                var entries = JsonSerializer.Deserialize<List<ChatLogEntryView>>(detail.ChatLog);
                if (entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        switch (entry.Role)
                        {
                            case "system":
                                AnsiConsole.MarkupLine("[dim]── system ──[/]");
                                AnsiConsole.MarkupLine($"[dim]{Truncate(entry.Content ?? "", 500).EscapeMarkup()}[/]");
                                AnsiConsole.WriteLine();
                                break;
                            case "user":
                                AnsiConsole.MarkupLine("[bold blue]── user ──[/]");
                                AnsiConsole.MarkupLine(entry.Content?.EscapeMarkup() ?? "");
                                AnsiConsole.WriteLine();
                                break;
                            case "assistant":
                                AnsiConsole.MarkupLine("[bold green]── assistant ──[/]");
                                AnsiConsole.MarkupLine(entry.Content?.EscapeMarkup() ?? "");
                                AnsiConsole.WriteLine();
                                break;
                            case "tool_call":
                                AnsiConsole.MarkupLine($"[yellow]→ tool call:[/] [bold]{entry.ToolName?.EscapeMarkup() ?? "?"}[/]");
                                break;
                            case "tool_result":
                                var status = entry.Success == true ? "[green]✓[/]" : "[red]✗[/]";
                                AnsiConsole.MarkupLine($"[yellow]← tool result:[/] {status} {entry.ToolCallId?.EscapeMarkup() ?? ""}");
                                AnsiConsole.WriteLine();
                                break;
                            case "error":
                                AnsiConsole.MarkupLine($"[red]── error ──[/]");
                                AnsiConsole.MarkupLine($"[red]{entry.Content?.EscapeMarkup() ?? ""}[/]");
                                AnsiConsole.WriteLine();
                                break;
                        }
                    }
                }
            }
            catch
            {
                // Fall back to raw JSON display
                AnsiConsole.MarkupLine("[yellow]Could not parse chat log. Raw JSON:[/]");
                AnsiConsole.WriteLine(detail.ChatLog);
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]No chat log recorded for this analysis.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to return...[/]");
        Console.ReadKey(true);
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

    private static string FormatTriageStatus(string status) => status switch
    {
        "completed" => "[green]✓ completed[/]",
        "pending" => "[yellow]… pending[/]",
        "failed" => "[red]✗ failed[/]",
        _ => status.EscapeMarkup(),
    };

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength), "…");
    }

    // View models for deserializing stored JSON
    private sealed class FlakyFindingView
    {
        [JsonPropertyName("TestName")]
        public string TestName { get; init; } = "";

        [JsonPropertyName("IsFlaky")]
        public bool IsFlaky { get; init; }

        [JsonPropertyName("Confidence")]
        public string Confidence { get; init; } = "";

        [JsonPropertyName("Diagnosis")]
        public string Diagnosis { get; init; } = "";

        [JsonPropertyName("ProposedFix")]
        public string? ProposedFix { get; init; }
    }

    private sealed class ChatLogEntryView
    {
        [JsonPropertyName("role")]
        public string Role { get; init; } = "";

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("toolName")]
        public string? ToolName { get; init; }

        [JsonPropertyName("toolCallId")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("success")]
        public bool? Success { get; init; }
    }
}
