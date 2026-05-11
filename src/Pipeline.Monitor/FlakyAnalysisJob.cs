using System.ComponentModel;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Pipeline.Monitor;

/// <summary>
/// Background job that analyzes builds with test failures to detect flaky tests.
/// Creates a fresh CopilotSession per build to keep context clean.
/// </summary>
public sealed class FlakyAnalysisJob
{
    private readonly CopilotClient _client;
    private readonly MonitorDatabase _db;
    private readonly MonitorLog _log;
    private readonly CancellationTokenSource _cts = new();
    private const string Source = "flaky";

    public FlakyAnalysisJob(CopilotClient client, MonitorDatabase db, MonitorLog log)
    {
        _client = client;
        _db = db;
        _log = log;
    }

    public Task StartAsync()
    {
        return Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop() => _cts.Cancel();

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _log.Info(Source, "Flaky analysis job started");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var targets = _db.GetBuildsForTriaging(limit: 1);

                if (targets.Count > 0)
                {
                    await AnalyzeBuildAsync(targets[0], cancellationToken);
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(Source, $"Unexpected error in analysis loop: {ex.Message}");
            }

            try
            {
                // Rate limit: wait 10 seconds between analyses
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.Info(Source, "Flaky analysis job stopped");
    }

    private const int MaxTestFailures = 10;

    private async Task AnalyzeBuildAsync(TriageTarget target, CancellationToken cancellationToken)
    {
        _log.Info(Source, $"Analyzing build {target.AzdoBuildId} ({target.Repository})");

        // Get test failures for this build (capped)
        var allFailures = _db.GetTestFailuresForBuild(target.AzdoBuildId);
        if (allFailures.Count == 0)
        {
            _db.SetTriageState(target.BuildId, "skipped");
            _log.Info(Source, $"Build {target.AzdoBuildId}: no test failures to analyze, skipping");
            return;
        }

        var testFailures = allFailures.Take(MaxTestFailures).ToList();
        if (allFailures.Count > MaxTestFailures)
        {
            _log.Warn(Source, $"Build {target.AzdoBuildId}: {allFailures.Count} failures, analyzing first {MaxTestFailures} only");
        }

        // Build context: test failures + their history, all pre-loaded
        var failureBlocks = new List<string>();
        foreach (var f in testFailures)
        {
            var block = $"### {f.TestName}\n- Outcome: {f.Outcome}";
            if (!string.IsNullOrWhiteSpace(f.ErrorMessage))
                block += $"\n- Error: {Truncate(f.ErrorMessage, 300)}";
            if (!string.IsNullOrWhiteSpace(f.StackTrace))
                block += $"\n- Stack: {Truncate(f.StackTrace, 300)}";

            // Pre-fetch history for this test
            var history = _db.GetTestHistoryForName(f.TestName, target.Repository);
            var existing = _db.GetFlakyTest(f.TestName, target.Repository);

            if (existing is not null)
            {
                block += $"\n- Already tracked as flaky: yes (occurrences: {existing.OccurrenceCount}, issue: {existing.IssueUrl ?? "none"})";
            }

            if (history.Count > 0)
            {
                block += $"\n- Failure history ({history.Count} occurrence(s)):";
                foreach (var h in history.Take(5))
                {
                    var branch = h.SourceBranch;
                    if (branch.StartsWith("refs/pull/"))
                        branch = $"PR #{branch.Split('/')[2]}";
                    else if (branch.StartsWith("refs/heads/"))
                        branch = branch["refs/heads/".Length..];
                    block += $"\n  - Build {h.AzdoBuildId} ({branch}, {h.BuildResult ?? "?"}, {h.FinishTime ?? "?"})";
                }
            }
            else
            {
                block += "\n- Failure history: first time seen";
            }

            failureBlocks.Add(block);
        }

        var failureBlock = string.Join("\n\n", failureBlocks);

        // Include timeline info if available
        var timelineBlock = "";
        if (target.TimelineJson is not null)
        {
            try
            {
                var timeline = JsonSerializer.Deserialize<TimelineSummary>(target.TimelineJson);
                if (timeline is not null)
                {
                    var jobLines = timeline.FailedJobs.Select(j => $"- {j.Name}: {j.Result} (worker: {j.WorkerName ?? "unknown"})");
                    var issueLines = timeline.Issues.Where(i => i.Type == "error").Take(10).Select(i => $"- [{i.Type}] {Truncate(i.Message, 200)}");
                    timelineBlock = $"""

                        Timeline - Failed Jobs:
                        {string.Join("\n", jobLines)}

                        Timeline - Errors:
                        {string.Join("\n", issueLines)}
                        """;
                }
            }
            catch
            {
                // Skip if timeline JSON is malformed
            }
        }

        // Determine PR number for triage request
        int? prNumber = null;
        if (target.SourceBranch.StartsWith("refs/pull/"))
        {
            if (int.TryParse(target.SourceBranch.Split('/')[2], out var pr))
                prNumber = pr;
        }

        // Create a triage request
        var triageRequestId = _db.InsertTriageRequest(target.BuildId, target.Repository, prNumber);

        // Track what the LLM finds
        var findings = new List<FlakyFinding>();

        try
        {
            var systemMessage = $"""
                You are a flaky test analyst. You are examining test failures from a single CI build to determine
                which failures are likely flaky (non-deterministic, not caused by the code change).

                Build information:
                - AzDO Build ID: {target.AzdoBuildId}
                - Repository: {target.Repository}
                - Branch: {target.SourceBranch}
                - Result: {target.Result ?? "unknown"}
                {timelineBlock}

                ## Test Failures (with history from other builds)

                {failureBlock}

                ## Instructions

                For EACH test failure above, call `record_flaky_determination` with your analysis.

                A test is likely flaky if:
                - It has failed in multiple builds across different PRs/branches
                - The error message is about timeouts, network issues, race conditions, or resource contention
                - It passes in some builds and fails in others with the same code

                If you need more information to make a determination, you have access to AzDO and Helix tools
                (e.g. `azdo_test_failures`, `helix_work_items_for_build`, `helix_console_for_build`) that you
                can use to dig deeper into the build or related builds.

                If you believe a fix is possible, describe it in the proposed_fix field.
                Be thorough but concise. Analyze EVERY test failure listed above.
                """;

            var tools = new List<AIFunction>
            {
                AIFunctionFactory.Create(
                    (
                        [Description("The full test name")] string testName,
                        [Description("Whether this test failure appears to be flaky")] bool isFlaky,
                        [Description("Confidence level: high, medium, or low")] string confidence,
                        [Description("Brief explanation of why it is or isn't flaky")] string diagnosis,
                        [Description("Proposed fix if one is apparent, null otherwise")] string? proposedFix
                    ) =>
                    {
                        findings.Add(new FlakyFinding
                        {
                            TestName = testName,
                            IsFlaky = isFlaky,
                            Confidence = confidence,
                            Diagnosis = diagnosis,
                            ProposedFix = proposedFix,
                        });

                        if (isFlaky)
                        {
                            _db.UpsertFlakyTest(testName, target.Repository, null, null);

                            if (!string.IsNullOrWhiteSpace(proposedFix))
                            {
                                _db.InsertFixRequest(triageRequestId, target.Repository, testName, diagnosis, proposedFix, null);
                            }

                            _log.Info(Source, $"Build {target.AzdoBuildId}: {testName} — FLAKY ({confidence}) — {Truncate(diagnosis, 100)}");
                        }
                        else
                        {
                            _log.Info(Source, $"Build {target.AzdoBuildId}: {testName} — not flaky ({confidence})");
                        }

                        return "Recorded.";
                    },
                    "record_flaky_determination",
                    "Record whether a test failure is flaky, with diagnosis and optional fix"),
            };

            await using var session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = "claude-sonnet-4.5",
                OnPermissionRequest = PermissionHandler.ApproveAll,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemMessage,
                },
                Tools = tools,
                SkillDirectories = SessionConfigHelper.SkillDirectories,
                McpServers = SessionConfigHelper.McpServers,
            });

            var tcs = new TaskCompletionSource();
            using var sub = session.On(evt =>
            {
                switch (evt)
                {
                    case SessionIdleEvent:
                        tcs.TrySetResult();
                        break;
                    case SessionErrorEvent err:
                        _log.Error(Source, $"Session error: {err.Data.Message}");
                        break;
                }
            });

            await session.SendAsync(new MessageOptions
            {
                Prompt = "Analyze all test failures in this build now.",
            });

            // Wait for the session to finish (with timeout)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            try
            {
                await tcs.Task;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Warn(Source, $"Build {target.AzdoBuildId}: analysis timed out after 5 minutes");
            }

            // Store the results
            var resultJson = JsonSerializer.Serialize(findings);
            _db.CompleteTriageRequest(triageRequestId, resultJson);
            _db.SetTriageState(target.BuildId, "triaged");

            var flakyCount = findings.Count(f => f.IsFlaky);
            _log.Info(Source, $"Build {target.AzdoBuildId}: triage complete — {flakyCount}/{findings.Count} flaky");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Error(Source, $"Build {target.AzdoBuildId}: analysis failed — {ex.Message}");
            _db.FailTriageRequest(triageRequestId, ex.Message);
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength
            ? singleLine
            : string.Concat(singleLine.AsSpan(0, maxLength), "…");
    }

    private sealed class FlakyFinding
    {
        public required string TestName { get; init; }
        public required bool IsFlaky { get; init; }
        public required string Confidence { get; init; }
        public required string Diagnosis { get; init; }
        public string? ProposedFix { get; init; }
    }
}
