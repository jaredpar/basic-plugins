using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace Pipeline.Monitor;

/// <summary>
/// Manages the polling CopilotSession. Configures it with a system prompt and
/// custom tools for querying AzDO/Helix and writing to SQLite. Buffers events
/// for the watch command to display.
/// </summary>
public sealed class PollingAgent : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly MonitorDatabase _db;
    private readonly MonitorConfig _config;
    private readonly ConcurrentQueue<AgentEvent> _eventLog = new();

    private CopilotSession? _session;
    private IDisposable? _eventSubscription;

    public PollingAgent(
        CopilotClient client,
        MonitorDatabase db,
        MonitorConfig config)
    {
        _client = client;
        _db = db;
        _config = config;
    }

    /// <summary>
    /// All events emitted by the polling agent, in order.
    /// </summary>
    public IReadOnlyCollection<AgentEvent> Events => _eventLog;

    /// <summary>
    /// Fired when a new event is added to the log.
    /// </summary>
    public event Action<AgentEvent>? EventReceived;

    public async Task StartAsync()
    {
        var pipelines = _config.Pipelines.Where(p => p.Enabled).ToList();
        var pipelineDescriptions = string.Join("\n", pipelines.Select(p =>
            $"- {p.Repository} (filter: {p.BuildFilter}, poll every {_config.PollIntervalMinutes} minutes)"));

        var systemMessage = $"""
            You are a build monitoring agent. Your job is to continuously poll AzDO for completed builds
            and record them in the database. You run in a loop, polling every {_config.PollIntervalMinutes} minutes.

            Pipelines to monitor:
            {pipelineDescriptions}

            Your workflow for each poll cycle:
            1. For each configured pipeline, use the `azdo_builds_for_repo` MCP tool to fetch recent completed builds
            2. For each build, call `check_build_exists` to see if it's already recorded
            3. For new builds, call `record_build` to save it to the database
            4. For builds with a result of "failed" or "partiallySucceeded", use the `azdo_test_failures` MCP tool
               to get failure details, then call `record_test_failures` to save them
            5. You may also use the `helix_work_items_for_build` MCP tool to get Helix-level details for failed builds
            6. After processing all pipelines, wait for the poll interval and repeat

            Be concise in your messages. Report what you found: how many new builds, how many with failures.
            When you find test failures, list the test names briefly.
            
            Start your first poll immediately.
            """;

        // Resolve paths relative to the executable location
        var appDir = AppContext.BaseDirectory;
        var skillsDir = Path.Combine(appDir, "skills");

        // The MCP server is the Pipeline.Mcp project. In development (artifacts layout),
        // it's in a sibling directory. We resolve it relative to our output location.
        var mcpServerDir = Path.GetFullPath(Path.Combine(appDir, "..", "..", "Pipeline.Mcp", "debug"));
        var mcpServerDll = Path.Combine(mcpServerDir, "Pipeline.Mcp.dll");

        _session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-sonnet-4.5",
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemMessage,
            },
            Tools = CreateTools(),
            SkillDirectories = [skillsDir],
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["basic-triage-mcp"] = new McpStdioServerConfig
                {
                    Command = "dotnet",
                    Args = [mcpServerDll],
                    Tools = ["*"],
                },
            },
        });

        // Subscribe to session events for the watch command
        _eventSubscription = _session.On(OnSessionEvent);

        // Send the initial message to kick off polling
        await _session.SendAsync(new MessageOptions
        {
            Prompt = "Begin monitoring. Start your first poll cycle now.",
        });
    }

    private void OnSessionEvent(SessionEvent evt)
    {
        string? message = evt switch
        {
            AssistantMessageEvent msg => msg.Data.Content,
            ToolExecutionStartEvent tool => $"[tool] Calling {tool.Data.ToolName}...",
            ToolExecutionCompleteEvent tool => $"[tool] {tool.Data.ToolCallId} {(tool.Data.Success ? "completed" : "failed")}",
            SessionErrorEvent err => $"[error] {err.Data.Message}",
            _ => null,
        };

        if (message is not null)
        {
            var agentEvent = new AgentEvent(DateTime.Now, message);
            _eventLog.Enqueue(agentEvent);
            EventReceived?.Invoke(agentEvent);
        }
    }

    private List<AIFunction> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(CheckBuildExists, "check_build_exists",
                "Check if a build with this AzDO build ID is already in the database"),

            AIFunctionFactory.Create(RecordBuild, "record_build",
                "Record a build in the monitoring database. Returns the database row ID."),

            AIFunctionFactory.Create(RecordTestFailures, "record_test_failures",
                "Record test failures for a build in the database"),
        ];
    }

    // --- Tool implementations (SQLite only) ---

    private bool CheckBuildExists(
        [Description("The AzDO build ID")] int azdoBuildId)
    {
        return _db.HasBuild(azdoBuildId);
    }

    private long RecordBuild(
        [Description("AzDO build ID")] int azdoBuildId,
        [Description("Repository in owner/repo format")] string repository,
        [Description("Build number string")] string buildNumber,
        [Description("Git source branch ref")] string sourceBranch,
        [Description("Pipeline definition name")] string definitionName,
        [Description("Build status")] string status,
        [Description("Build result (succeeded, failed, etc.)")] string? result,
        [Description("Whether the build has test failures")] bool hasTestFailures)
    {
        return _db.InsertBuild(
            azdoBuildId, repository, buildNumber, sourceBranch,
            definitionName, status, result, null, hasTestFailures);
    }

    private int RecordTestFailures(
        [Description("Database row ID of the build")] long buildId,
        [Description("JSON array of test failures with testName, outcome, errorMessage fields")] string failuresJson)
    {
        var failures = JsonSerializer.Deserialize<List<TestFailureInput>>(failuresJson, s_jsonOptions) ?? [];
        foreach (var f in failures)
        {
            _db.InsertTestFailure(buildId, f.TestName, f.Outcome, f.ErrorMessage);
        }
        return failures.Count;
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private sealed class TestFailureInput
    {
        public required string TestName { get; init; }
        public required string Outcome { get; init; }
        public string? ErrorMessage { get; init; }
    }
}

/// <summary>
/// A timestamped event from the polling agent.
/// </summary>
public sealed record AgentEvent(DateTime Timestamp, string Message);
