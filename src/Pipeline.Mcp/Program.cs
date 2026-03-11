using Azure.Identity;
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

var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions()
{
    TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
});
var helix = await HelixClient.CreateAsync(credential);
builder.Services.AddSingleton(helix);

var azdoClient = await AzdoClient.CreateAsync(credential);
builder.Services.AddSingleton(azdoClient);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
