using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace Pipeline.Monitor;

/// <summary>
/// Shared prompt infrastructure for commands that need the LLM to identify builds.
/// Both `add` and `retry` use identical LLM sessions — they only differ in what
/// the C# code does with the emitted builds.
/// </summary>
public static class BuildPromptHelper
{
    /// <summary>
    /// Runs a Copilot session that fetches builds based on a user prompt and calls
    /// the provided handler for each emitted build.
    /// </summary>
    public static async Task<List<EmittedBuild>> RunBuildPromptAsync(CopilotClient client, List<AIFunction> pipelineTools, string prompt)
    {
        var emittedBuilds = new List<EmittedBuild>();

        var systemMessage = """
            You are a build import agent. The user will describe what builds they want.

            Your workflow:
            1. Interpret the user's request to determine the repository, count, and filter (PR/CI/all)
            2. Use the `azdo_builds_for_repo` MCP tool to fetch the builds
            3. For EACH build returned, call `emit_build` with the build metadata
            4. After emitting all builds, report how many you found

            IMPORTANT: You MUST call `emit_build` once for every build. That is how builds get recorded.
            Do not summarize or skip builds. Emit every single one.
            """;

        var tools = new List<AIFunction>
        {
            AIFunctionFactory.Create(
                ([Description("JSON object with build fields: azdoBuildId, repository, buildNumber, sourceBranch, definitionName, status, result")] string buildJson) =>
                {
                    var build = JsonSerializer.Deserialize<EmittedBuild>(buildJson, s_jsonOptions);
                    if (build is null)
                        return "error: could not parse build JSON";
                    emittedBuilds.Add(build);
                    return $"ok: queued build {build.AzdoBuildId}";
                },
                "emit_build",
                """
                Queue a build for processing. Pass a JSON object:
                { "azdoBuildId": 123, "repository": "dotnet/roslyn", "buildNumber": "20240101.1",
                  "sourceBranch": "refs/pull/123/merge", "definitionName": "roslyn-CI",
                  "status": "completed", "result": "failed" }
                """),
        };
        tools.AddRange(pipelineTools);

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-sonnet-4.5",
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemMessage,
            },
            Tools = tools,
            SkillDirectories = SessionConfigHelper.SkillDirectories,
        });

        var done = new TaskCompletionSource();
        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    if (msg.Data.Content is { Length: > 0 } content)
                        AnsiConsole.MarkupLine(content.EscapeMarkup());
                    break;
                case ToolExecutionStartEvent tool:
                    AnsiConsole.MarkupLine($"[dim][tool] {tool.Data.ToolName.EscapeMarkup()}...[/]");
                    break;
                case SessionErrorEvent err:
                    AnsiConsole.MarkupLine($"[red][error] {err.Data.Message.EscapeMarkup()}[/]");
                    done.TrySetResult();
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
            }
        });

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await done.Task;

        return emittedBuilds;
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new() { PropertyNameCaseInsensitive = true };
}

public sealed class EmittedBuild
{
    [JsonPropertyName("azdoBuildId")]
    public int AzdoBuildId { get; init; }

    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("buildNumber")]
    public string BuildNumber { get; init; } = "";

    [JsonPropertyName("sourceBranch")]
    public string SourceBranch { get; init; } = "";

    [JsonPropertyName("definitionName")]
    public string DefinitionName { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("result")]
    public string? Result { get; init; }
}
