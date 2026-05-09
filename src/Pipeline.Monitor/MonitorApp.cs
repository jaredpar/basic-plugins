using GitHub.Copilot.SDK;
using Pipeline.Core;
using Spectre.Console;

namespace Pipeline.Monitor;

/// <summary>
/// Main application class. Creates a CopilotClient, starts the polling agent session,
/// and runs the interactive command loop.
/// </summary>
public sealed class MonitorApp : IAsyncDisposable
{
    private readonly MonitorConfig _config;
    private readonly MonitorDatabase _db;
    private readonly AzdoClient _azdoClient;
    private readonly HelixClient _helixClient;
    private readonly CopilotClient _client;
    private readonly MonitorLog _log;
    private readonly PollingAgent _pollingAgent;
    private readonly FailureCollectionJob _collectionJob;
    private readonly CancellationTokenSource _cts = new();

    public MonitorApp(MonitorConfig config)
    {
        _config = config;
        _db = MonitorDatabase.Open(config.Database);
        _log = new MonitorLog();

        var credential = PipelineUtils.CreateCredential();
        _azdoClient = AzdoClient.Create(credential);
        _helixClient = HelixClient.Create(credential);

        _client = new CopilotClient();
        _pollingAgent = new PollingAgent(_client, _db, config, _log);
        _collectionJob = new FailureCollectionJob(_db, _azdoClient, _helixClient, _log);
    }

    public async Task RunAsync()
    {
        AnsiConsole.MarkupLine("[bold blue]Pipeline Monitor[/]");
        AnsiConsole.MarkupLine($"Database: [dim]{Path.GetFullPath(_config.Database)}[/]");

        var enabledPipelines = _config.Pipelines.Where(p => p.Enabled).ToList();
        foreach (var p in enabledPipelines)
        {
            AnsiConsole.MarkupLine($"Monitoring: [bold]{p.Repository}[/] (filter: {p.BuildFilter})");
        }

        AnsiConsole.WriteLine();

        // Start the Copilot client, polling agent, and failure collection job
        await _client.StartAsync();
        await _pollingAgent.StartAsync();
        _ = _collectionJob.StartAsync();

        AnsiConsole.MarkupLine("[green]Polling agent started.[/]");
        AnsiConsole.MarkupLine("[green]Failure collection job started.[/]");
        AnsiConsole.MarkupLine("Type [bold]help[/] for available commands.");
        AnsiConsole.WriteLine();

        // Command loop
        while (!_cts.IsCancellationRequested)
        {
            var input = AnsiConsole.Prompt(
                new TextPrompt<string>("[bold]monitor>[/] ")
                    .AllowEmpty());

            var command = input.Trim().ToLowerInvariant();

            switch (command)
            {
                case "watch":
                    await Commands.WatchCommand.ExecuteAsync(_log, _cts.Token);
                    break;

                case "builds":
                    Commands.BuildsCommand.Execute(_db);
                    break;

                case "add":
                    await Commands.AddCommand.ExecuteAsync(_client, _db);
                    break;

                case "retry":
                    await Commands.RetryCommand.ExecuteAsync(_client, _db);
                    break;

                case "help":
                    PrintHelp();
                    break;

                case "quit" or "exit":
                    AnsiConsole.MarkupLine("[dim]Shutting down...[/]");
                    _cts.Cancel();
                    break;

                case "":
                    break;

                default:
                    AnsiConsole.MarkupLine($"[red]Unknown command:[/] {command}. Type [bold]help[/] for available commands.");
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        var table = new Table().NoBorder();
        table.AddColumn(new TableColumn("Command").PadRight(4));
        table.AddColumn("Description");
        table.AddRow("[bold]watch[/]", "Live-tail the polling agent output");
        table.AddRow("[bold]builds[/]", "Show builds in the database");
        table.AddRow("[bold]add[/]", "Import builds from AzDO using a natural language prompt");
        table.AddRow("[bold]retry[/]", "Retry failure collection for builds that previously failed");
        table.AddRow("[bold]help[/]", "Show this help message");
        table.AddRow("[bold]quit[/]", "Shut down the monitor");
        AnsiConsole.Write(table);
    }

    public async ValueTask DisposeAsync()
    {
        _collectionJob.Stop();
        await _pollingAgent.DisposeAsync();
        await _client.StopAsync();
        _db.Dispose();
        _cts.Dispose();
    }
}
