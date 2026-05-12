using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Pipeline.Core;
using Pipeline.Mcp.Core;

namespace Pipeline.Monitor;

/// <summary>
/// Manages the polling CopilotSession. Configures it with a system prompt and
/// custom tools for querying AzDO/Helix and writing to SQLite. Logs events
/// to the shared MonitorLog.
/// </summary>
public sealed class PollingAgent : IAsyncDisposable
{
    private readonly CopilotClient _client;
    private readonly MonitorClient _db;
    private readonly MonitorConfig _config;
    private readonly MonitorLog _log;
    private readonly List<AIFunction> _pipelineTools;
    private const string Source = "poller";

    private CopilotSession? _session;
    private IDisposable? _eventSubscription;

    public PollingAgent(
        CopilotClient client,
        MonitorClient db,
        MonitorConfig config,
        MonitorLog log,
        List<AIFunction> pipelineTools)
    {
        _client = client;
        _db = db;
        _config = config;
        _log = log;
        _pipelineTools = pipelineTools;
    }

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
            2. For each build, call `db_check_build_exists` to see if it's already recorded
            3. For new builds, call `db_record_build` to save it to the database
            4. After processing all pipelines, wait for the poll interval and repeat

            Do NOT collect test failures or Helix data — that is handled automatically by a separate collection job.

            Be concise in your messages. Report what you found: how many new builds were recorded.
            
            Start your first poll immediately.
            """;

        // Resolve paths relative to the executable location
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
            SkillDirectories = SessionConfigHelper.SkillDirectories,
        });

        // Subscribe to session events and forward to the shared log
        _eventSubscription = _session.On(OnSessionEvent);

        _log.Info(Source, "Polling agent started");

        // Send the initial message to kick off polling
        await _session.SendAsync(new MessageOptions
        {
            Prompt = "Begin monitoring. Start your first poll cycle now.",
        });
    }

    private void OnSessionEvent(SessionEvent evt)
    {
        switch (evt)
        {
            case AssistantMessageEvent msg:
                _log.Info(Source, msg.Data.Content);
                break;
            case ToolExecutionStartEvent tool:
                _log.Tool(Source, $"Calling {tool.Data.ToolName}...");
                break;
            case ToolExecutionCompleteEvent tool:
                _log.Tool(Source, $"{tool.Data.ToolCallId} {(tool.Data.Success ? "completed" : "failed")}");
                break;
            case SessionErrorEvent err:
                _log.Error(Source, err.Data.Message);
                break;
        }
    }

    private List<AIFunction> CreateTools()
    {
        var tools = new List<AIFunction>(_pipelineTools);
        tools.AddRange(MonitorToolFactory.Create(_db));
        return tools;
    }

    public async ValueTask DisposeAsync()
    {
        _eventSubscription?.Dispose();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }
    }
}

