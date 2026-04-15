using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure.Core;
using Kusto.Data;
using Kusto.Data.Exceptions;
using Kusto.Data.Net.Client;
using Microsoft.Identity.Client.NativeInterop;

namespace Pipeline.Core;

public class HelixWorkItem
{
    [JsonPropertyName("friendlyName")]
    public required string FriendlyName { get; init; }
    [JsonPropertyName("executionTime")]
    public int ExecutionTime { get; init; }
    [JsonPropertyName("queueName")]
    public required string QueueName { get; init; }
    [JsonPropertyName("queuedTime")]
    public int QueuedTime { get; init; }
    [JsonPropertyName("azdoBuildId")]
    public int AzdoBuildId { get; init; }
    [JsonPropertyName("azdoPhaseName")]
    public required string AzdoPhaseName { get; init; }
    [JsonPropertyName("azdoAttempt")]
    public int AzdoAttempt { get; init; }
    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }
    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }
    [JsonPropertyName("consoleUri")]
    public required string ConsoleUri { get; init; }
    [JsonPropertyName("jobId")]
    public long JobId { get; init; }
    [JsonPropertyName("jobName")]
    public required string JobName { get; init; }
    [JsonPropertyName("finished")]
    public DateTime Finished { get; init; }
    [JsonPropertyName("workItemId")]
    public long WorkItemId { get; init; }
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public class HelixWorkItemConsole
{
    [JsonPropertyName("jobId")]
    public long JobId { get; init; }
    [JsonPropertyName("workItemId")]
    public long WorkItemId { get; init; }
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public class HelixWorkItemFile
{
    [JsonPropertyName("jobId")]
    public long JobId { get; init; }
    [JsonPropertyName("workItemId")]
    public long WorkItemId { get; init; }
    [JsonPropertyName("jobName")]
    public required string JobName { get; init; }
    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }
}

public sealed class HelixClient
{
    private const string ClusterUrl = "https://engsrvprod.kusto.windows.net";
    private const string DatabaseName = "engineeringdata";

    private static readonly TokenRequestContext s_kustoTokenContext = new(["https://kusto.kusto.windows.net/.default"]);

    private KustoConnectionStringBuilder KustoConnectionStringBuilder { get; }

    private HelixClient(TokenCredential tokenCredential)
    {
        KustoConnectionStringBuilder = new KustoConnectionStringBuilder(ClusterUrl, DatabaseName)
            .WithAadTokenProviderAuthentication(() =>
                tokenCredential.GetToken(s_kustoTokenContext, default).Token);
    }

    /// <summary>
    /// Creates a new <see cref="HelixClient"/>. Authentication is deferred
    /// until the first Kusto query is executed.
    /// </summary>
    public static HelixClient Create(TokenCredential tokenCredential) => new(tokenCredential);

    /// <inheritdoc cref="Create"/>
    public static Task<HelixClient> CreateAsync(TokenCredential tokenCredential) =>
        Task.FromResult(Create(tokenCredential));

    public Task<List<HelixWorkItem>> GetHelixWorkItemsForBuildAsync(string owner, string repository, int buildId, bool includeAll = false)
    {
        var failedFilter = includeAll ? "" : "| where ExitCode != 0";
        string query = $"""
            Jobs
            | where Repository == "{owner}/{repository}"
            | project-away Started, Finished
            | join kind=inner WorkItems on JobId
            | extend p = parse_json(Properties)
            | extend AzdoBuildId = toint(p["BuildId"])
            | where AzdoBuildId == {buildId}
            | extend AzdoPhaseName = tostring(p["System.PhaseName"])
            | extend AzdoAttempt = tostring(p["System.JobAttempt"])
            | extend ExecutionTime = (Finished - Started) / 1s
            | extend QueuedTime = (Started - Queued) / 1s
            {failedFilter}
            | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName, ExitCode, ConsoleUri, JobId, JobName, QueueName, Finished, WorkItemId, Status
            """;

        return QueryHelixWorkItem(query);
    }

    public Task<List<HelixWorkItem>> GetHelixWorkItemsForBuildAsync(string owner, string repository, string buildNumber, bool includeAll = false)
    {
        var failedFilter = includeAll ? "" : "| where ExitCode != 0";
        string query = $"""
            Jobs
            | where Repository == "{owner}/{repository}"
            | project-away Started, Finished
            | join kind=inner WorkItems on JobId
            | extend p = parse_json(Properties)
            | extend AzdoBuildId = toint(p["BuildId"])
            | where tostring(p["BuildNumber"]) == "{buildNumber}"
            | extend AzdoPhaseName = tostring(p["System.PhaseName"])
            | extend AzdoAttempt = tostring(p["System.JobAttempt"])
            | extend ExecutionTime = (Finished - Started) / 1s
            | extend QueuedTime = (Started - Queued) / 1s
            {failedFilter}
            | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName, ExitCode, ConsoleUri, JobId, JobName, QueueName, Finished, WorkItemId, Status
            """;

        return QueryHelixWorkItem(query);
    }

    public Task<List<HelixWorkItem>> GetHelixWorkItemsForPullRequestAsync(string owner, string repository, int prNumber, bool includeAll = false)
    {
        var failedFilter = includeAll ? "" : "| where ExitCode != 0";
        string query = $"""
            Jobs
            | where Repository == "{owner}/{repository}"
            | where Branch == "refs/pull/{prNumber}/merge"
            | project-away Started, Finished
            | join kind=inner WorkItems on JobId
            | extend p = parse_json(Properties)
            | extend AzdoPhaseName = tostring(p["System.PhaseName"])
            | extend AzdoAttempt = tostring(p["System.JobAttempt"])
            | extend AzdoBuildId = toint(p["BuildId"])
            | extend ExecutionTime = (Finished - Started) / 1s
            | extend QueuedTime = (Started - Queued) / 1s
            {failedFilter}
            | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName, ExitCode, ConsoleUri, JobId, JobName, QueueName, Finished, WorkItemId, Status
            """;

        return QueryHelixWorkItem(query);
    }

    public async Task<HelixWorkItem> GetHelixWorkItemAsync(long jobId, long workItemId)
    {
        string query = $"""
            WorkItems
            | where JobId == {jobId}
            | where WorkItemId == {workItemId}
            | join kind=inner Jobs on JobId
            | extend p = parse_json(Properties)
            | extend AzdoPhaseName = tostring(p["System.PhaseName"])
            | extend AzdoAttempt = tostring(p["System.JobAttempt"])
            | extend AzdoBuildId = toint(p["BuildId"])
            | extend ExecutionTime = (Finished - Started) / 1s
            | extend QueuedTime = (Started - Queued) / 1s
            | project FriendlyName, ExecutionTime, QueuedTime, AzdoBuildId, AzdoPhaseName, AzdoAttempt, MachineName, ExitCode, ConsoleUri, JobId, JobName, QueueName, Finished, WorkItemId, Status
            """;

        var items = await QueryHelixWorkItem(query);
        return items.Single();
    }

    private async Task<List<HelixWorkItem>> QueryHelixWorkItem(string query)
    {
        try
        {
            using var kustoQueryClient = KustoClientFactory.CreateCslQueryProvider(KustoConnectionStringBuilder);
            var reader = kustoQueryClient.ExecuteQuery(query);
            var list = new List<HelixWorkItem>();

            // Read and print results
            while (reader.Read())
            {
                var friendlyName = reader.GetString(0);
                var executionTime = TimeSpan.FromSeconds(reader.GetDouble(1));
                var queuedTime = TimeSpan.FromSeconds(reader.GetDouble(2));
                var azdoBuildId = reader.GetInt32(3);
                var azdoPhaseName = reader.GetString(4);
                var azdoAttempt = int.Parse(reader.GetString(5));
                var machineName = reader.GetString(6);
                var exitCode = reader.GetInt32(7);
                var consoleUri = reader.GetString(8);
                var jobId = reader.GetInt64(9);
                var jobName = reader.GetString(10);
                var queueName = reader.GetString(11);
                var finished = reader.GetDateTime(12);
                var workItemId = reader.GetInt64(13);
                var status = reader.GetString(14);

                list.Add(new HelixWorkItem
                {
                    FriendlyName = friendlyName,
                    ExecutionTime = (int)executionTime.TotalSeconds,
                    QueuedTime = (int)queuedTime.TotalSeconds,
                    AzdoBuildId = azdoBuildId,
                    AzdoPhaseName = azdoPhaseName,
                    AzdoAttempt = azdoAttempt,
                    MachineName = machineName,
                    ExitCode = exitCode,
                    ConsoleUri = consoleUri,
                    JobId = jobId,
                    JobName = jobName,
                    QueueName = queueName,
                    Finished = finished,
                    WorkItemId = workItemId,
                    Status = status
                });
            }

            return list;
        }
        catch (KustoRequestException ex) when (ex.ErrorReason == "Unauthorized")
        {
            Console.WriteLine("Error: access denied. You are not authorized to query this Kusto database. Ensure your account has been granted access.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("Error reading Kusto, are you connected to the VPN?");
            throw;
        }
    }

    public async Task<HelixWorkItemConsole> GetConsoleAsync(HelixWorkItem workItem)
    {
        using var httpClient = new HttpClient();
        var text = await httpClient.GetStringAsync(workItem.ConsoleUri);
        return new HelixWorkItemConsole
        {
            JobId = workItem.JobId,
            WorkItemId = workItem.WorkItemId,
            Text = text
        };
    }

    public async Task<List<HelixWorkItemConsole>> GetConsolesAsync(List<HelixWorkItem> workItems)
    {
        using var httpClient = new HttpClient();
        var list = new List<HelixWorkItemConsole>();
        foreach (var workItem in workItems)
        {
            var text = await httpClient.GetStringAsync(workItem.ConsoleUri);
            list.Add(new HelixWorkItemConsole
            {
                JobId = workItem.JobId,
                WorkItemId = workItem.WorkItemId,
                Text = text
            });
        }
        return list;
    }

    public async Task<List<HelixWorkItemFile>> GetFilesAsync(HelixWorkItem workItem)
    {
        return await GetFilesAsync([workItem]);
    }

    public async Task<List<HelixWorkItemFile>> GetFilesAsync(List<HelixWorkItem> workItems)
    {
        if (workItems.Count == 0)
            return [];

        var jobNameMap = workItems.ToDictionary(w => w.WorkItemId, w => w.JobName);
        var workItemIds = string.Join(", ", workItems.Select(w => w.WorkItemId));
        string query = $"""
            Files
            | where WorkItemId in ({workItemIds})
            | project JobId, WorkItemId, FileName, Uri, SizeBytesLong
            """;

        try
        {
            using var kustoQueryClient = KustoClientFactory.CreateCslQueryProvider(KustoConnectionStringBuilder);
            var reader = kustoQueryClient.ExecuteQuery(query);
            var list = new List<HelixWorkItemFile>();

            while (reader.Read())
            {
                var jobId = reader.GetInt64(0);
                var workItemId = reader.GetInt64(1);
                var fileName = reader.GetString(2);
                var uri = reader.GetString(3);
                var sizeBytes = reader.GetInt64(4);

                var jobName = jobNameMap.GetValueOrDefault(workItemId, "unknown");

                list.Add(new HelixWorkItemFile
                {
                    JobId = jobId,
                    WorkItemId = workItemId,
                    JobName = jobName,
                    FileName = fileName,
                    Uri = uri,
                    SizeBytes = sizeBytes
                });
            }

            return list;
        }
        catch (KustoRequestException ex) when (ex.ErrorReason == "Unauthorized")
        {
            Console.WriteLine("Error: access denied. You are not authorized to query this Kusto database. Ensure your account has been granted access.");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("Error reading Kusto, are you connected to the VPN?");
            throw;
        }
    }

    public static async Task DownloadFilesAsync(List<HelixWorkItemFile> files, string outputDir)
    {
        using var httpClient = new HttpClient();
        foreach (var file in files)
        {
            var dir = Path.Combine(outputDir, file.JobName, file.WorkItemId.ToString());
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, file.FileName);
            var bytes = await httpClient.GetByteArrayAsync(file.Uri);
            await File.WriteAllBytesAsync(filePath, bytes);
        }
    }
}