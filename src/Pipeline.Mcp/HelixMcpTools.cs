using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Pipeline.Core;

namespace Pipeline.Mcp;

[McpServerToolType]
public class HelixMcpTools
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "helix_work_items_for_build"), Description("Get failed Helix work items for an AzDo build. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixWorkItemsForBuild(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = int.TryParse(buildId, out var id)
            ? await helix.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
            : await helix.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
        return JsonSerializer.Serialize(items, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_work_items_for_pr"), Description("Get failed Helix work items for a pull request. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixWorkItemsForPullRequest(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The pull request number")] int prNumber,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = await helix.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
        return JsonSerializer.Serialize(items, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_console_for_build"), Description("Get console output for failed Helix work items in an AzDo build. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixConsoleForBuild(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = int.TryParse(buildId, out var id)
            ? await helix.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
            : await helix.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
        var consoles = await helix.GetConsolesAsync(items);
        return JsonSerializer.Serialize(consoles, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_console_for_pr"), Description("Get console output for failed Helix work items in a pull request. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixConsoleForPullRequest(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The pull request number")] int prNumber,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = await helix.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
        var consoles = await helix.GetConsolesAsync(items);
        return JsonSerializer.Serialize(consoles, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_console_for_work_item"), Description("Get console output for a specific Helix work item by job ID and work item ID")]
    public static async Task<string> GetHelixConsoleForWorkItem(
        HelixClient helix,
        [Description("The Helix job ID")] long jobId,
        [Description("The Helix work item ID")] long workItemId)
    {
        var workItem = await helix.GetHelixWorkItemAsync(jobId, workItemId);
        var console = await helix.GetConsoleAsync(workItem);
        return JsonSerializer.Serialize(console, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_files_for_build"), Description("Get file metadata for failed Helix work items in an AzDo build. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixFilesForBuild(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The AzDO build ID (integer like 1379081)")] string buildId,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = int.TryParse(buildId, out var id)
            ? await helix.GetHelixWorkItemsForBuildAsync(owner, repository, id, includeAll)
            : await helix.GetHelixWorkItemsForBuildAsync(owner, repository, buildId, includeAll);
        var files = await helix.GetFilesAsync(items);
        return JsonSerializer.Serialize(files, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_files_for_pr"), Description("Get file metadata for failed Helix work items in a pull request. Returns only failed items by default which is the desired behavior in almost all cases.")]
    public static async Task<string> GetHelixFilesForPullRequest(
        HelixClient helix,
        [Description("The repository owner (e.g. dotnet)")] string owner,
        [Description("The repository name (e.g. roslyn)")] string repository,
        [Description("The pull request number")] int prNumber,
        [Description("WARNING: Do not set to true unless the user explicitly asks for succeeded/passing work items. This is an expensive query. Default (false) returns only failed items which is correct for nearly all use cases.")] bool includeAll = false)
    {
        var items = await helix.GetHelixWorkItemsForPullRequestAsync(owner, repository, prNumber, includeAll);
        var files = await helix.GetFilesAsync(items);
        return JsonSerializer.Serialize(files, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_files_for_work_item"), Description("Get file metadata for a specific Helix work item by job ID and work item ID")]
    public static async Task<string> GetHelixFilesForWorkItem(
        HelixClient helix,
        [Description("The Helix job ID")] long jobId,
        [Description("The Helix work item ID")] long workItemId)
    {
        var workItem = await helix.GetHelixWorkItemAsync(jobId, workItemId);
        var files = await helix.GetFilesAsync(workItem);
        return JsonSerializer.Serialize(files, s_jsonOptions);
    }
}
