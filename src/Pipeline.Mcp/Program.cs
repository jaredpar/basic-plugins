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

var credential = PipelineUtils.CreateCredential();

HelixClient helix;
AzdoClient azdoClient;
try
{
    helix = await HelixClient.CreateAsync(credential);
    azdoClient = await AzdoClient.CreateAsync(credential);
}
catch (AuthenticationFailedException ex)
{
    Console.Error.WriteLine("Error: Authentication failed. Ensure you are logged in (e.g., with 'az login').");
    Console.Error.WriteLine(ex.Message);
    return;
}

builder.Services.AddSingleton(helix);
builder.Services.AddSingleton(azdoClient);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
