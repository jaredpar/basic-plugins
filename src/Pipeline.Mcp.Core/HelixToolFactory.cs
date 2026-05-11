using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Pipeline.Core;

namespace Pipeline.Mcp.Core;

/// <summary>
/// Creates <see cref="AIFunction"/> instances that mirror the Helix MCP tools.
/// Allows agents to call Helix functions directly without an MCP server process.
/// </summary>
public static class HelixToolFactory
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    public static List<AIFunction> Create(HelixClient client)
    {
        return
        [
            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The AzDO build ID (integer like 1379081)")] string buildId,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = int.TryParse(buildId, out var id)
                        ? await client.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
                        : await client.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
                    return JsonSerializer.Serialize(items, s_jsonOptions);
                },
                "helix_work_items_for_build",
                "Get failed Helix work items for an AzDo build. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The pull request number")] int prNumber,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = await client.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
                    return JsonSerializer.Serialize(items, s_jsonOptions);
                },
                "helix_work_items_for_pr",
                "Get failed Helix work items for a pull request. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The AzDO build ID (integer like 1379081)")] string buildId,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = int.TryParse(buildId, out var id)
                        ? await client.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
                        : await client.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
                    var consoles = await client.GetConsolesAsync(items);
                    return JsonSerializer.Serialize(consoles, s_jsonOptions);
                },
                "helix_console_for_build",
                "Get console output for failed Helix work items in an AzDo build. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The pull request number")] int prNumber,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = await client.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
                    var consoles = await client.GetConsolesAsync(items);
                    return JsonSerializer.Serialize(consoles, s_jsonOptions);
                },
                "helix_console_for_pr",
                "Get console output for failed Helix work items in a pull request. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The Helix job ID")] long jobId,
                    [Description("The Helix work item ID")] long workItemId) =>
                {
                    var workItem = await client.GetHelixWorkItemAsync(jobId, workItemId);
                    var console = await client.GetConsoleAsync(workItem);
                    return JsonSerializer.Serialize(console, s_jsonOptions);
                },
                "helix_console_for_work_item",
                "Get console output for a specific Helix work item by job ID and work item ID."),

            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The AzDO build ID (integer like 1379081)")] string buildId,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = int.TryParse(buildId, out var id)
                        ? await client.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
                        : await client.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
                    var files = await client.GetFilesAsync(items);
                    return JsonSerializer.Serialize(files, s_jsonOptions);
                },
                "helix_files_for_build",
                "Get file metadata for failed Helix work items in an AzDo build. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The repository owner (e.g. dotnet)")] string owner,
                    [Description("The repository name (e.g. roslyn)")] string repository,
                    [Description("The pull request number")] int prNumber,
                    [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. Default (false) returns only failed items.")] bool includeAll) =>
                {
                    var items = await client.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
                    var files = await client.GetFilesAsync(items);
                    return JsonSerializer.Serialize(files, s_jsonOptions);
                },
                "helix_files_for_pr",
                "Get file metadata for failed Helix work items in a pull request. Returns only failed items by default."),

            AIFunctionFactory.Create(
                async (
                    [Description("The Helix job ID")] long jobId,
                    [Description("The Helix work item ID")] long workItemId) =>
                {
                    var workItem = await client.GetHelixWorkItemAsync(jobId, workItemId);
                    var files = await client.GetFilesAsync(workItem);
                    return JsonSerializer.Serialize(files, s_jsonOptions);
                },
                "helix_files_for_work_item",
                "Get file metadata for a specific Helix work item by job ID and work item ID."),
        ];
    }
}
