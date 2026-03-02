using Dolphin.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Dolphin.Mcp;

public static class McpServer
{
    public static async Task RunAsync()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddMcpServer()
                    .WithStdioServerTransport()
                    .WithTools<SetupSemgrepTool>()
                    .WithTools<RunCheckTool>();

                services.AddLogging(logging =>
                {
                    // MCP servers must not write to stdout (reserved for JSON-RPC),
                    // so redirect all logs to stderr only.
                    logging.ClearProviders();
                    logging.AddConsole(opts =>
                    {
                        opts.LogToStandardErrorThreshold = LogLevel.Trace;
                    });
                    logging.SetMinimumLevel(LogLevel.Warning);
                });
            });

        var host = builder.Build();
        await host.RunAsync();
    }
}
