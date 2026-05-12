using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Spectre.Console;
using Pipeline.Core;
using Pipeline.Mcp.Core;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Starts an interactive chat session with the LLM. The session has access to all
/// pipeline, database, and skill tools. Type "quit" to exit back to the main menu.
/// </summary>
public static class ConsoleCommand
{
    private enum OutputMode { None, Message, Reasoning, Tool }

    public static async Task ExecuteAsync(
        CopilotClient client,
        MonitorClient db,
        List<AIFunction> pipelineTools)
    {
        var tools = new List<AIFunction>(pipelineTools);
        tools.AddRange(MonitorToolFactory.Create(db));

        var systemMessage = """
            You are a pipeline investigation assistant. You have access to:
            - **azdo_** tools: Query Azure DevOps builds, test failures, timelines, and artifacts
            - **helix_** tools: Query Helix work items, console logs, and file metadata
            - **db_** tools: Query and update the local monitoring database (builds, test failures, flaky tests, triage results)

            Help the user investigate build failures, analyze test results, search Helix logs,
            and understand patterns in their CI pipeline data. Be thorough but concise.
            """;

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "claude-opus-4.6",
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Streaming = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemMessage,
            },
            Tools = tools,
            SkillDirectories = SessionConfigHelper.SkillDirectories,
        });

        AnsiConsole.MarkupLine("[bold blue]Console session started[/] (model: claude-opus-4.6)");
        AnsiConsole.MarkupLine("[dim]Type \"quit\" to return to the main menu.[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold cyan]>[/] ")
                    .AllowEmpty());

            if (string.IsNullOrWhiteSpace(input))
                continue;

            if (input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[dim]Session ended.[/]");
                break;
            }

            var done = new TaskCompletionSource();
            var currentMode = OutputMode.None;

            void SwitchMode(OutputMode newMode)
            {
                if (currentMode == newMode)
                    return;

                // End the previous mode
                if (currentMode != OutputMode.None)
                {
                    Console.ResetColor();
                    Console.WriteLine();
                }

                // Start the new mode
                if (newMode == OutputMode.Reasoning)
                    Console.ForegroundColor = ConsoleColor.DarkYellow;

                currentMode = newMode;
            }

            using var sub = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        if (delta.Data.DeltaContent is { Length: > 0 } chunk)
                        {
                            SwitchMode(OutputMode.Message);
                            Console.Write(chunk);
                        }
                        break;
                    case AssistantReasoningDeltaEvent reasoningDelta:
                        if (reasoningDelta.Data.DeltaContent is { Length: > 0 } reasoningChunk)
                        {
                            SwitchMode(OutputMode.Reasoning);
                            Console.Write(reasoningChunk);
                        }
                        break;
                    case AssistantMessageEvent:
                    case AssistantReasoningEvent:
                    case ToolExecutionCompleteEvent:
                        break;
                    case ToolExecutionStartEvent tool:
                        SwitchMode(OutputMode.Tool);
                        AnsiConsole.MarkupLine($"[dim][tool] {tool.Data.ToolName.EscapeMarkup()}...[/]");
                        break;
                    case SessionErrorEvent err:
                        SwitchMode(OutputMode.None);
                        AnsiConsole.MarkupLine($"[red][error] {err.Data.Message.EscapeMarkup()}[/]");
                        done.TrySetResult();
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                }
            });

            // Hook Ctrl+C to abort the current message instead of killing the process
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                _ = session.AbortAsync();
            };
            Console.CancelKeyPress += cancelHandler;

            try
            {
                await session.SendAsync(new MessageOptions { Prompt = input });
                await done.Task;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }

            Console.ResetColor();
            if (currentMode != OutputMode.None)
                Console.WriteLine();
            Console.WriteLine();
        }
    }
}
