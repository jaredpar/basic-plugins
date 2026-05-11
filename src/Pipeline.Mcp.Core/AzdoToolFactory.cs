using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pipeline.Core;

namespace Pipeline.Mcp.Core;

/// <summary>
/// Creates <see cref="AIFunction"/> instances that mirror the AzDO MCP tools.
/// Allows agents to call AzDO functions directly without an MCP server process.
/// </summary>
public static class AzdoToolFactory
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static List<AIFunction> Create(AzdoClient client)
    {
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description("The GitHub repository in owner/repo format (e.g. dotnet/roslyn)")] string repository,
                    [Description("Maximum number of builds to return (default 10)")] int top,
                    [Description("Filter builds: 'pr' for pull request builds only, 'ci' for post-merge builds only, 'all' for both (default)")] string filter) =>
                {
                    string? reasonFilter = filter.ToLowerInvariant() switch
                    {
                        "pr" => "pullRequest",
                        "ci" => "individualCI,batchedCI",
                        _ => null,
                    };
                    var builds = await client.GetBuildsForRepositoryAsync(repository, top, reasonFilter);
                    return JsonSerializer.Serialize(builds, s_jsonOptions);
                },
                "azdo_builds_for_repo",
                "Get AzDO builds for a GitHub repository. Returns both PR and CI builds by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("Optional pipeline definition ID to filter by")] int? definitionId,
                    [Description("Maximum number of builds to return (default 10)")] int top) =>
                {
                    var builds = await client.GetRecentBuildsAsync(definitionId, top);
                    return JsonSerializer.Serialize(builds, s_jsonOptions);
                },
                "azdo_recent_builds",
                "Get recent AzDO builds, optionally filtered by pipeline definition ID."),

            AIFunctionFactory.Create(
                async (
                    [Description("The GitHub repository in owner/repo format (e.g. dotnet/roslyn)")] string repository,
                    [Description("The pull request number")] int prNumber,
                    [Description("Maximum number of builds to return (default 10)")] int top) =>
                {
                    var builds = await client.GetBuildsForPullRequestAsync(repository, prNumber, top);
                    return JsonSerializer.Serialize(builds, s_jsonOptions);
                },
                "azdo_pr_builds",
                "Get AzDO builds for a specific pull request."),

            AIFunctionFactory.Create(
                async ([Description("The AzDO build ID (integer like 1379081)")] string buildId) =>
                {
                    var failures = int.TryParse(buildId, out var id)
                        ? await client.GetTestFailuresAsync(id)
                        : await client.GetTestFailuresAsync(buildId);
                    return JsonSerializer.Serialize(failures, s_jsonOptions);
                },
                "azdo_test_failures",
                "Get test failures for an AzDO build."),

            AIFunctionFactory.Create(
                async ([Description("The AzDO build ID (integer like 1379081)")] string buildId) =>
                {
                    var summaries = int.TryParse(buildId, out var id)
                        ? await client.GetTestSummaryByJobAsync(id)
                        : await client.GetTestSummaryByJobAsync(buildId);
                    return JsonSerializer.Serialize(summaries, s_jsonOptions);
                },
                "azdo_test_summary",
                "Get test counts for each job (test run) in an AzDO build."),

            AIFunctionFactory.Create(
                async ([Description("The AzDO build ID (integer like 1379081)")] string buildId) =>
                {
                    var timeline = int.TryParse(buildId, out var id)
                        ? await client.GetTimelineAsync(id)
                        : await client.GetTimelineAsync(buildId);
                    return JsonSerializer.Serialize(timeline, s_jsonOptions);
                },
                "azdo_timeline",
                "Get the timeline (all records) for an AzDO build."),

            AIFunctionFactory.Create(
                async ([Description("The AzDO build ID (integer like 1379081)")] string buildId) =>
                {
                    var artifacts = int.TryParse(buildId, out var id)
                        ? await client.GetArtifactsAsync(id)
                        : await client.GetArtifactsAsync(buildId);
                    return JsonSerializer.Serialize(artifacts, s_jsonOptions);
                },
                "azdo_artifacts",
                "Get build artifacts for an AzDO build."),

            AIFunctionFactory.Create(
                async ([Description("The AzDO build ID (integer like 1379081)")] string buildId) =>
                {
                    var timeline = int.TryParse(buildId, out var id)
                        ? await client.GetTimelineAsync(id)
                        : await client.GetTimelineAsync(buildId);
                    var jobs = timeline.Records
                        .Where(r => r.RecordType == "Job")
                        .OrderBy(r => r.Order)
                        .ToList();
                    return JsonSerializer.Serialize(jobs, s_jsonOptions);
                },
                "azdo_jobs",
                "Get job records from an AzDO build timeline."),
        ];
    }
}
