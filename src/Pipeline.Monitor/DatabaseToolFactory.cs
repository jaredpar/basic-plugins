using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Pipeline.Monitor;

/// <summary>
/// Creates <see cref="AIFunction"/> instances that expose the monitor SQLite database
/// to AI agents. Allows agents to query and record build, test failure, helix, and
/// flaky test data without hand-wiring tool methods in each agent.
/// </summary>
public static class DatabaseToolFactory
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static List<AIFunction> Create(MonitorDatabase db)
    {
        return
        [
            // --- Read operations ---

            AIFunctionFactory.Create(
                ([Description("The AzDO build ID")] int azdoBuildId) =>
                    db.HasBuild(azdoBuildId),
                "db_check_build_exists",
                "Check if a build with this AzDO build ID is already in the monitoring database."),

            AIFunctionFactory.Create(
                ([Description("The AzDO build ID")] int azdoBuildId) =>
                    JsonSerializer.Serialize(db.GetBuildByAzdoId(azdoBuildId), s_jsonOptions),
                "db_get_build",
                "Get a build record by its AzDO build ID. Returns build info, collection state, and test failure count."),

            AIFunctionFactory.Create(
                ([Description("Maximum number of builds to return (default 50)")] int limit) =>
                    JsonSerializer.Serialize(db.GetRecentBuilds(limit), s_jsonOptions),
                "db_get_recent_builds",
                "Get recent builds from the monitoring database, ordered by most recent first."),

            AIFunctionFactory.Create(
                ([Description("The AzDO build ID")] int azdoBuildId) =>
                    JsonSerializer.Serialize(db.GetTestFailuresForBuild(azdoBuildId), s_jsonOptions),
                "db_get_test_failures_for_build",
                "Get test failures recorded for a build by its AzDO build ID. Returns test names, outcomes, error messages, and stack traces."),

            AIFunctionFactory.Create(
                ([Description("The AzDO build ID")] int azdoBuildId) =>
                    JsonSerializer.Serialize(db.GetHelixWorkItemsForBuild(azdoBuildId), s_jsonOptions),
                "db_get_helix_work_items_for_build",
                "Get Helix work items recorded for a build by its AzDO build ID. Returns work item names, exit codes, console summaries, and URIs."),

            AIFunctionFactory.Create(
                (
                    [Description("The full test name")] string testName,
                    [Description("Repository in owner/repo format")] string repository,
                    [Description("Maximum number of history entries to return (default 20)")] int limit
                ) =>
                    JsonSerializer.Serialize(db.GetTestHistoryForName(testName, repository, limit), s_jsonOptions),
                "db_get_test_history",
                "Get failure history for a specific test across builds. Shows when/where the test has failed before."),

            AIFunctionFactory.Create(
                (
                    [Description("The full test name")] string testName,
                    [Description("Repository in owner/repo format")] string repository
                ) =>
                    JsonSerializer.Serialize(db.GetFlakyTest(testName, repository), s_jsonOptions),
                "db_get_flaky_test",
                "Get the flaky test record for a test name and repository. Returns occurrence count, issue info, and first/last seen dates."),

            AIFunctionFactory.Create(
                () => JsonSerializer.Serialize(db.GetAllFlakyTests(), s_jsonOptions),
                "db_get_all_flaky_tests",
                "Get all tracked flaky tests, ordered by most recently seen."),

            AIFunctionFactory.Create(
                ([Description("Maximum number of results to return (default 50)")] int limit) =>
                    JsonSerializer.Serialize(db.GetRecentTriageResults(limit), s_jsonOptions),
                "db_get_recent_triage_results",
                "Get recent completed triage results with build info."),

            // --- Write operations ---

            AIFunctionFactory.Create(
                (
                    [Description("AzDO build ID")] int azdoBuildId,
                    [Description("Repository in owner/repo format")] string repository,
                    [Description("Build number string")] string buildNumber,
                    [Description("Git source branch ref")] string sourceBranch,
                    [Description("Pipeline definition name")] string definitionName,
                    [Description("Build status")] string status,
                    [Description("Build result (succeeded, failed, etc.)")] string? result
                ) =>
                    db.InsertBuild(azdoBuildId, repository, buildNumber, sourceBranch,
                        definitionName, status, result, null),
                "db_record_build",
                "Record a build in the monitoring database. Returns the database row ID. Test failure status is determined automatically by the collection job."),

            AIFunctionFactory.Create(
                (
                    [Description("Database row ID of the build")] long buildId,
                    [Description("JSON array of test failures with testName, outcome, errorMessage fields")] string failuresJson
                ) =>
                {
                    var failures = JsonSerializer.Deserialize<List<TestFailureInput>>(failuresJson, s_jsonOptions) ?? [];
                    foreach (var f in failures)
                    {
                        db.InsertTestFailure(buildId, f.TestName, f.Outcome, f.ErrorMessage, comment: f.Comment);
                    }
                    return failures.Count;
                },
                "db_record_test_failures",
                "Record test failures for a build in the database. Accepts a JSON array of failures."),

            AIFunctionFactory.Create(
                (
                    [Description("The full test name")] string testName,
                    [Description("Repository in owner/repo format")] string repository,
                    [Description("GitHub issue number if one exists")] int? issueNumber,
                    [Description("GitHub issue URL if one exists")] string? issueUrl
                ) =>
                    db.UpsertFlakyTest(testName, repository, issueNumber, issueUrl),
                "db_upsert_flaky_test",
                "Record or update a flaky test entry. Increments occurrence count if already exists. Returns the row ID."),

            AIFunctionFactory.Create(
                ([Description("The AzDO build ID")] int azdoBuildId) =>
                    db.ResetBuildCollectionData(azdoBuildId),
                "db_reset_build_collection",
                "Reset collection data for a build. Deletes all test failures and helix work items, resets both AzDO and Helix collection states to pending. The collection jobs will re-collect the data on their next pass."),

        ];
    }

    private sealed class TestFailureInput
    {
        [System.Text.Json.Serialization.JsonPropertyName("testName")]
        public required string TestName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("outcome")]
        public required string Outcome { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("comment")]
        public string? Comment { get; init; }
    }
}
