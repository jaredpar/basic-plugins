using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Starts an interactive chat session with the LLM. The session has access to all
/// pipeline, database, and skill tools. Type "quit" to exit back to the main menu.
/// </summary>
public static class ConsoleCommand
{
    public static async Task ExecuteAsync(
        CopilotClient client,
        MonitorDatabase db,
        List<AIFunction> pipelineTools)
    {
        var tools = new List<AIFunction>(pipelineTools);
        tools.AddRange(DatabaseToolFactory.Create(db));

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
            Model = "claude-opus-4.7",
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemMessage,
            },
            Tools = tools,
            SkillDirectories = SessionConfigHelper.SkillDirectories,
        });

        AnsiConsole.MarkupLine("[bold blue]Console session started[/] (model: claude-opus-4.7)");
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
            var hadContent = false;

            using var sub = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        if (msg.Data.Content is { Length: > 0 } content)
                        {
                            if (!hadContent)
                            {
                                Console.WriteLine();
                                hadContent = true;
                            }
                            Console.Write(content);
                        }
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

            await session.SendAsync(new MessageOptions { Prompt = input });
            await done.Task;

            if (hadContent)
                Console.WriteLine();
            Console.WriteLine();
        }
    }
}
