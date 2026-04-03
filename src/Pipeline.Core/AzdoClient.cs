using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;

namespace Pipeline.Core;

public class AzdoBuild
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("buildNumber")]
    public required string BuildNumber { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("sourceBranch")]
    public required string SourceBranch { get; init; }

    [JsonPropertyName("definitionName")]
    public required string DefinitionName { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTime? FinishTime { get; init; }
}

public class AzdoTimelineIssue
{
    public required string Type { get; init; }
    public required string Message { get; init; }
    public string? Category { get; init; }
}

public class AzdoTimelineRecord
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Name { get; init; }
    public required string RecordType { get; init; }
    public int Order { get; init; }
    public string? State { get; init; }
    public string? Result { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? FinishTime { get; init; }
    public List<AzdoTimelineIssue> Issues { get; init; } = [];
    public string? WorkerName { get; init; }
    public string? LogUrl { get; init; }
}

public class AzdoTimeline
{
    public required List<AzdoTimelineRecord> Records { get; init; }

    /// <summary>All issues (errors and warnings) across all records.</summary>
    public List<AzdoTimelineIssue> GetIssues() =>
        Records.SelectMany(r => r.Issues).ToList();

    /// <summary>Names of all Job records.</summary>
    public List<string> GetJobNames() =>
        Records.Where(r => r.RecordType == "Job").Select(r => r.Name).ToList();

    /// <summary>Get direct children of a record (or top-level records if parentId is null).</summary>
    public List<AzdoTimelineRecord> GetChildren(string? parentId) =>
        Records.Where(r => r.ParentId == parentId).OrderBy(r => r.Order).ToList();
}

public class AzdoArtifact
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string? DownloadUrl { get; init; }
    public string? ResourceType { get; init; }
}

public class AzdoJobTestSummary
{
    public required string JobName { get; init; }
    public int TotalCount { get; init; }
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
}

public class AzdoTestFailure
{
    [JsonPropertyName("testCaseTitle")]
    public required string TestCaseTitle { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("testRunId")]
    public int TestRunId { get; init; }

    [JsonPropertyName("testRunName")]
    public required string TestRunName { get; init; }
}

public sealed class AzdoClient
{
    public const string DefaultOrganization = "dnceng-public";
    public const string DefaultProject = "public";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient HttpClient { get; }
    private string Organization { get; }
    private string Project { get; }

    private AzdoClient(HttpClient httpClient, string organization, string project)
    {
        HttpClient = httpClient;
        Organization = organization;
        Project = project;
    }

    private static void EnsureAuthenticatedResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new PipelineAuthenticationException(
                "Authentication failed: the Azure DevOps request returned 401 Unauthorized. " +
                "Ensure your account has access and you are logged in (e.g., with 'az login').");
        }

        response.EnsureSuccessStatusCode();
    }

    public static async Task<AzdoClient> CreateAsync(
        TokenCredential tokenCredential,
        string organization = DefaultOrganization,
        string project = DefaultProject)
    {
        var tokenRequestContext = new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]);
        var token = await tokenCredential.GetTokenAsync(tokenRequestContext, default);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"https://dev.azure.com/{organization}/{project}/"),
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        return new AzdoClient(httpClient, organization, project);
    }

    private string GetBuildUri(int buildId) =>
        $"https://dev.azure.com/{Organization}/{Project}/_build/results?buildId={buildId}";

    public async Task<List<AzdoBuild>> GetRecentBuildsAsync(int? definitionId = null, int top = 10)
    {
        var url = $"_apis/build/builds?api-version=7.1&$top={top}";
        if (definitionId is not null)
        {
            url += $"&definitions={definitionId}";
        }

        var response = await HttpClient.GetAsync(url);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoBuild>> GetBuildsForRepositoryAsync(string repository, int top = 10, string? reasonFilter = null)
    {
        var url = $"_apis/build/builds?api-version=7.1&$top={top}&repositoryId={Uri.EscapeDataString(repository)}&repositoryType=GitHub";
        if (reasonFilter is not null)
        {
            url += $"&reasonFilter={Uri.EscapeDataString(reasonFilter)}";
        }

        var response = await HttpClient.GetAsync(url);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoBuild>> GetBuildsForPullRequestAsync(string repository, int prNumber, int top = 10)
    {
        var branchName = $"refs/pull/{prNumber}/merge";
        var url = $"_apis/build/builds?api-version=7.1&$top={top}&branchName={Uri.EscapeDataString(branchName)}&repositoryId={Uri.EscapeDataString(repository)}&repositoryType=GitHub";

        var response = await HttpClient.GetAsync(url);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<AzdoListResponse<AzdoBuildRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize builds response");

        return result.Value.Select(b => new AzdoBuild
        {
            Id = b.Id,
            BuildNumber = b.BuildNumber,
            Status = b.Status,
            Result = b.Result,
            Uri = GetBuildUri(b.Id),
            SourceBranch = b.SourceBranch,
            DefinitionName = b.Definition?.Name ?? "unknown",
            FinishTime = b.FinishTime,
        }).ToList();
    }

    public async Task<List<AzdoTestFailure>> GetTestFailuresAsync(int buildId)
    {
        var buildUri = $"vstfs:///Build/Build/{buildId}";
        var runsUrl = $"_apis/test/runs?api-version=7.1&buildUri={Uri.EscapeDataString(buildUri)}";

        var runsResponse = await HttpClient.GetAsync(runsUrl);
        EnsureAuthenticatedResponse(runsResponse);

        var runsJson = await runsResponse.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestRun>>(runsJson, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test runs response");

        var failures = new List<AzdoTestFailure>();
        foreach (var run in runs.Value)
        {
            var resultsUrl = $"_apis/test/Runs/{run.Id}/results?api-version=7.1&outcomes=Failed";
            var resultsResponse = await HttpClient.GetAsync(resultsUrl);
            EnsureAuthenticatedResponse(resultsResponse);

            var resultsJson = await resultsResponse.Content.ReadAsStringAsync();
            var results = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestResult>>(resultsJson, s_jsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize test results response");

            foreach (var r in results.Value)
            {
                failures.Add(new AzdoTestFailure
                {
                    TestCaseTitle = r.TestCaseTitle,
                    Outcome = r.Outcome,
                    ErrorMessage = r.ErrorMessage,
                    StackTrace = r.StackTrace,
                    TestRunId = run.Id,
                    TestRunName = run.Name,
                });
            }
        }

        return failures;
    }

    public async Task<List<AzdoJobTestSummary>> GetTestSummaryByJobAsync(int buildId)
    {
        var buildUri = $"vstfs:///Build/Build/{buildId}";
        var runsUrl = $"_apis/test/runs?api-version=7.1&includeRunDetails=true&buildUri={Uri.EscapeDataString(buildUri)}";

        var response = await HttpClient.GetAsync(runsUrl);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var runs = JsonSerializer.Deserialize<AzdoListResponse<AzdoTestRun>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize test runs response");

        return runs.Value.Select(r => new AzdoJobTestSummary
        {
            JobName = r.Name,
            TotalCount = r.TotalTests,
            PassedCount = r.PassedTests,
            FailedCount = r.TotalTests - r.PassedTests - r.NotApplicableTests,
            SkippedCount = r.NotApplicableTests,
        }).ToList();
    }

    public async Task<AzdoTimeline> GetTimelineAsync(int buildId)
    {
        var url = $"_apis/build/builds/{buildId}/timeline?api-version=7.1";
        var response = await HttpClient.GetAsync(url);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var raw = JsonSerializer.Deserialize<AzdoTimelineRaw>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize timeline response");

        return new AzdoTimeline
        {
            Records = (raw.Records ?? []).Select(r => new AzdoTimelineRecord
            {
                Id = r.Id,
                ParentId = r.ParentId,
                Name = r.Name,
                RecordType = r.Type,
                Order = r.Order,
                State = r.State,
                Result = r.Result,
                ErrorCount = r.ErrorCount,
                WarningCount = r.WarningCount,
                StartTime = r.StartTime,
                FinishTime = r.FinishTime,
                WorkerName = r.WorkerName,
                LogUrl = r.Log?.Url,
                Issues = (r.Issues ?? []).Select(i => new AzdoTimelineIssue
                {
                    Type = i.Type,
                    Message = i.Message,
                    Category = i.Category,
                }).ToList(),
            }).ToList(),
        };
    }

    public async Task<List<AzdoArtifact>> GetArtifactsAsync(int buildId)
    {
        var url = $"_apis/build/builds/{buildId}/artifacts?api-version=7.1";
        var response = await HttpClient.GetAsync(url);
        EnsureAuthenticatedResponse(response);

        var json = await response.Content.ReadAsStringAsync();
        var raw = JsonSerializer.Deserialize<AzdoListResponse<AzdoArtifactRaw>>(json, s_jsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize artifacts response");

        return raw.Value.Select(a => new AzdoArtifact
        {
            Id = a.Id,
            Name = a.Name,
            DownloadUrl = a.Resource?.DownloadUrl,
            ResourceType = a.Resource?.Type,
        }).ToList();
    }

    public async Task DownloadArtifactAsync(int buildId, string artifactName, string outputPath)
    {
        var artifacts = await GetArtifactsAsync(buildId);
        var artifact = artifacts.FirstOrDefault(a => a.Name == artifactName)
            ?? throw new InvalidOperationException($"Artifact '{artifactName}' not found for build {buildId}");

        var downloadUrl = artifact.DownloadUrl
            ?? throw new InvalidOperationException($"Artifact '{artifactName}' has no download URL");

        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        EnsureAuthenticatedResponse(response);

        using var fileStream = File.Create(outputPath);
        await response.Content.CopyToAsync(fileStream);
    }

    // Internal types for JSON deserialization of raw API responses

    private class AzdoListResponse<T>
    {
        [JsonPropertyName("count")]
        public int Count { get; init; }

        [JsonPropertyName("value")]
        public required List<T> Value { get; init; }
    }

    private class AzdoBuildRaw
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("buildNumber")]
        public required string BuildNumber { get; init; }

        [JsonPropertyName("status")]
        public required string Status { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("uri")]
        public required string Uri { get; init; }

        [JsonPropertyName("sourceBranch")]
        public required string SourceBranch { get; init; }

        [JsonPropertyName("definition")]
        public AzdoBuildDefinition? Definition { get; init; }

        [JsonPropertyName("finishTime")]
        public DateTime? FinishTime { get; init; }
    }

    private class AzdoBuildDefinition
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    private class AzdoTestRun
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("totalTests")]
        public int TotalTests { get; init; }

        [JsonPropertyName("passedTests")]
        public int PassedTests { get; init; }

        [JsonPropertyName("unanalyzedTests")]
        public int UnanalyzedTests { get; init; }

        [JsonPropertyName("notApplicableTests")]
        public int NotApplicableTests { get; init; }
    }

    private class AzdoTestResult
    {
        [JsonPropertyName("testCaseTitle")]
        public required string TestCaseTitle { get; init; }

        [JsonPropertyName("outcome")]
        public required string Outcome { get; init; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; init; }

        [JsonPropertyName("stackTrace")]
        public string? StackTrace { get; init; }
    }

    private class AzdoTimelineRaw
    {
        [JsonPropertyName("records")]
        public List<AzdoTimelineRecordRaw>? Records { get; init; }
    }

    private class AzdoTimelineRecordRaw
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("parentId")]
        public string? ParentId { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("order")]
        public int Order { get; init; }

        [JsonPropertyName("state")]
        public string? State { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("errorCount")]
        public int ErrorCount { get; init; }

        [JsonPropertyName("warningCount")]
        public int WarningCount { get; init; }

        [JsonPropertyName("startTime")]
        public DateTime? StartTime { get; init; }

        [JsonPropertyName("finishTime")]
        public DateTime? FinishTime { get; init; }

        [JsonPropertyName("workerName")]
        public string? WorkerName { get; init; }

        [JsonPropertyName("issues")]
        public List<AzdoTimelineIssueRaw>? Issues { get; init; }

        [JsonPropertyName("log")]
        public AzdoBuildLogReference? Log { get; init; }
    }

    private class AzdoTimelineIssueRaw
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("message")]
        public required string Message { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }
    }

    private class AzdoBuildLogReference
    {
        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    private class AzdoArtifactRaw
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("resource")]
        public AzdoArtifactResourceRaw? Resource { get; init; }
    }

    private class AzdoArtifactResourceRaw
    {
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; init; }

        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}
