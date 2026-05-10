using Spectre.Console;

namespace Pipeline.Monitor.Commands;

/// <summary>
/// Live-tails the shared monitor log. Press q or Esc to return to the prompt.
/// </summary>
public static class WatchCommand
{
    public static async Task ExecuteAsync(MonitorLog log, CancellationToken appToken)
    {
        AnsiConsole.MarkupLine("[dim]Watching monitor log. Press Esc to stop.[/]");
        AnsiConsole.WriteLine();

        // Print any existing entries first
        foreach (var existing in log.Entries)
        {
            PrintEntry(existing);
        }

        // Subscribe to new entries
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);

        void OnEntry(LogEntry entry) => PrintEntry(entry);
        log.EntryAdded += OnEntry;

        try
        {
            // Poll for key presses while watching
            while (!watchCts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
                        break;
                }

                try
                {
                    await Task.Delay(100, watchCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            log.EntryAdded -= OnEntry;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Stopped watching.[/]");
    }

    private static void PrintEntry(LogEntry entry)
    {
        var timestamp = entry.Timestamp.ToString("HH:mm:ss");
        var source = entry.Source.EscapeMarkup();
        var message = entry.Message.EscapeMarkup();

        var (levelColor, sourceColor) = entry.Level switch
        {
            LogLevel.Error => ("red", "red"),
            LogLevel.Warn => ("yellow", "yellow"),
            LogLevel.Tool => ("cyan", "cyan"),
            _ => ("white", "blue"),
        };

        AnsiConsole.MarkupLine($"[dim]{timestamp}[/] [{sourceColor}]{source}[/] [{levelColor}]{message}[/]");
    }
}
