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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools(tools);

await builder.Build().RunAsync();
