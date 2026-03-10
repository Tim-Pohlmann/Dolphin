using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Lsp;
using Dolphin.Mcp;

namespace Dolphin;

internal static class Startup
{
    internal static async Task<int> RunAsync(string[] args, Stream? inputStream = null, Stream? outputStream = null)
    {
        if (args is ["serve", "--stdio"])
        {
            await McpServer.RunAsync();
            return 0;
        }

        if (args is ["lsp"])
            return await LspServer.RunAsync(inputStream, outputStream);

        var root = new RootCommand("Dolphin — custom static analysis powered by Opengrep")
        {
            CheckCommand.Build()
        };

        root.Name = "dolphin";
        return await root.InvokeAsync(args);
    }
}
