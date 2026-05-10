using System.Text.Json.Serialization;

namespace Pipeline.Monitor;

/// <summary>
/// Summary of AzDO build timeline data stored as a JSON blob on the build record.
/// Contains only diagnostic-relevant information: failed jobs and error/warning issues.
/// </summary>
public sealed class TimelineSummary
{
    [JsonPropertyName("failedJobs")]
    public List<TimelineJobEntry> FailedJobs { get; init; } = [];

    [JsonPropertyName("issues")]
    public List<TimelineIssueEntry> Issues { get; init; } = [];
}

public sealed class TimelineJobEntry
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("workerName")]
    public string? WorkerName { get; init; }
}

public sealed class TimelineIssueEntry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }
}
