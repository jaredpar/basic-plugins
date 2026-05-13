using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Pipeline.Core;

namespace Pipeline.Mcp;

[McpServerToolType]
public class HelixMcpTools
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    [McpServerTool(Name = "helix_jobs"), Description("List Helix jobs matching filter criteria. Use source, type, and/or build to narrow results.")]
    public static async Task<string> GetHelixJobs(
        HelixClient helix,
        [Description("The job source (e.g. 'official/dotnet/runtime/main')")] string? source = null,
        [Description("The job type (e.g. 'test/functional/cli')")] string? type = null,
        [Description("The build number")] string? build = null,
        [Description("The job name/correlation ID")] string? name = null,
        [Description("Maximum number of jobs to return")] int count = 20)
    {
        var jobs = await helix.GetJobsAsync(source, type, build, name, count: count);
        return JsonSerializer.Serialize(jobs, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_work_items"), Description("List all work items for a Helix job by job name.")]
    public static async Task<string> GetHelixWorkItems(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName)
    {
        var items = await helix.GetWorkItemsAsync(jobName);
        return JsonSerializer.Serialize(items, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_work_item_details"), Description("Get detailed information about a specific Helix work item including logs, files, errors, exit code, and machine name.")]
    public static async Task<string> GetHelixWorkItemDetails(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var workItem = await helix.GetWorkItemAsync(jobName, workItemName);
        return JsonSerializer.Serialize(workItem, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_console"), Description("Get console output for a specific Helix work item.")]
    public static async Task<string> GetHelixConsole(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var console = await helix.GetConsoleAsync(jobName, workItemName);
        return JsonSerializer.Serialize(console, s_jsonOptions);
    }

    [McpServerTool(Name = "helix_files"), Description("List files uploaded from a specific Helix work item.")]
    public static async Task<string> GetHelixFiles(
        HelixClient helix,
        [Description("The Helix job name (correlation ID)")] string jobName,
        [Description("The work item name")] string workItemName)
    {
        var files = await helix.GetFilesAsync(jobName, workItemName);
        return JsonSerializer.Serialize(files, s_jsonOptions);
    }
}
