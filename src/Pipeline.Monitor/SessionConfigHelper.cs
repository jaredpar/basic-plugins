using GitHub.Copilot.SDK;

namespace Pipeline.Monitor;

/// <summary>
/// Resolves paths to skills, agents, and the MCP server relative to the executable location.
/// Provides shared session configuration that all Copilot sessions in the monitor use.
/// </summary>
public static class SessionConfigHelper
{
    /// <summary>
    /// Directory containing the skill SKILL.md files.
    /// </summary>
    public static string SkillsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "skills");

    /// <summary>
    /// Directory containing the agent .md prompt files.
    /// </summary>
    public static string AgentsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "agents");

    /// <summary>
    /// Path to the Pipeline.Mcp.dll MCP server. Resolved relative to the artifacts layout.
    /// </summary>
    public static string McpServerDll { get; } = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Pipeline.Mcp", "debug")),
        "Pipeline.Mcp.dll");

    /// <summary>
    /// The shared MCP server configurations used by all sessions.
    /// </summary>
    public static Dictionary<string, McpServerConfig> McpServers { get; } = new()
    {
        ["basic-triage-mcp"] = new McpStdioServerConfig
        {
            Command = "dotnet",
            Args = [McpServerDll],
            Tools = ["*"],
        },
    };

    /// <summary>
    /// The shared skill directories used by all sessions.
    /// </summary>
    public static List<string> SkillDirectories { get; } = [SkillsDirectory];

    /// <summary>
    /// Reads an agent prompt file from the agents directory.
    /// </summary>
    public static string ReadAgentPrompt(string agentFileName)
    {
        var path = Path.Combine(AgentsDirectory, agentFileName);
        return File.ReadAllText(path);
    }
}
