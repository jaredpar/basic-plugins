using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Pipeline.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var credential = PipelineUtils.CreateCredential();
builder.Services.AddSingleton(HelixClient.Create(credential));
builder.Services.AddSingleton(AzdoClient.Create(credential));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
