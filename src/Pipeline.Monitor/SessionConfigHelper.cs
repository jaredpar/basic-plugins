using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Pipeline.Core;
using Pipeline.Mcp.Core;

namespace Pipeline.Monitor;

/// <summary>
/// Resolves paths to skills and agents relative to the executable location.
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
    /// The shared skill directories used by all sessions.
    /// </summary>
    public static List<string> SkillDirectories { get; } = [SkillsDirectory];

    /// <summary>
    /// Creates the combined list of AzDO + Helix AI tools for use in Copilot sessions.
    /// </summary>
    public static List<AIFunction> CreatePipelineTools(AzdoClient azdoClient, HelixClient helixClient)
    {
        var tools = new List<AIFunction>();
        tools.AddRange(AzdoToolFactory.Create(azdoClient));
        tools.AddRange(HelixToolFactory.Create(helixClient));
        return tools;
    }

    /// <summary>
    /// Reads an agent prompt file from the agents directory.
    /// </summary>
    public static string ReadAgentPrompt(string agentFileName)
    {
        var path = Path.Combine(AgentsDirectory, agentFileName);
        return File.ReadAllText(path);
    }
}
