using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Live-tails the polling agent output. Press Ctrl+C to return to the prompt.
/// </summary>
public static class WatchCommand
{
    public static async Task ExecuteAsync(PollingAgent agent, CancellationToken appToken)
    {
        AnsiConsole.MarkupLine("[dim]Watching polling agent output. Press Ctrl+C to stop.[/]");
        AnsiConsole.WriteLine();

        // Print any existing events first
        foreach (var existing in agent.Events)
        {
            PrintEvent(existing);
        }

        // Subscribe to new events
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        var token = watchCts.Token;

        void OnEvent(AgentEvent evt) => PrintEvent(evt);
        agent.EventReceived += OnEvent;

        try
        {
            // Wait until Ctrl+C is pressed (caught as OperationCanceledException)
            Console.CancelKeyPress += OnCancel;
            void OnCancel(object? sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true; // Don't terminate the process
                watchCts.Cancel();
            }

            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException) when (!appToken.IsCancellationRequested)
            {
                // Ctrl+C pressed — return to prompt
            }
            finally
            {
                Console.CancelKeyPress -= OnCancel;
            }
        }
        finally
        {
            agent.EventReceived -= OnEvent;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Stopped watching.[/]");
    }

    private static void PrintEvent(AgentEvent evt)
    {
        var timestamp = evt.Timestamp.ToString("HH:mm:ss");
        var message = evt.Message.EscapeMarkup();

        if (message.StartsWith("[tool]"))
        {
            AnsiConsole.MarkupLine($"[dim]{timestamp}[/] [cyan]{message}[/]");
        }
        else if (message.StartsWith("[error]"))
        {
            AnsiConsole.MarkupLine($"[dim]{timestamp}[/] [red]{message}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]{timestamp}[/] {message}");
        }
    }
}
