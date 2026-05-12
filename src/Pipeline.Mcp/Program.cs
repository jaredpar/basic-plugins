using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Pipeline.Core;
using Pipeline.Mcp.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var credential = PipelineUtils.CreateCredential();
var helixClient = HelixClient.Create(credential);
var azdoClient = AzdoClient.Create(credential);

builder.Services.AddSingleton(helixClient);
builder.Services.AddSingleton(azdoClient);

var tools = new List<McpServerTool>();
foreach (var func in AzdoToolFactory.Create(azdoClient))
{
    tools.Add(McpServerTool.Create(func));
}
foreach (var func in HelixToolFactory.Create(helixClient))
{
    tools.Add(McpServerTool.Create(func));
}

// Database tools (optional — only if database exists)
var dbPath = GetDatabasePath(args);
if (dbPath is not null && File.Exists(dbPath))
{
    var db = MonitorClient.Open(dbPath);
    builder.Services.AddSingleton(db);
    foreach (var func in MonitorToolFactory.Create(db))
    {
        tools.Add(McpServerTool.Create(func));
    }
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();

static string? GetDatabasePath(string[] args)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--database")
            return args[i + 1];
    }

    var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    var baseDir = string.IsNullOrEmpty(xdg)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
        : xdg;
    return Path.Combine(baseDir, "pipeline-monitor", "monitor.db");
}

