using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<CliExecutionService>();
builder.Services.AddSingleton<OrchestratorTools>();
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<OrchestratorTools>();

await builder.Build().RunAsync();
