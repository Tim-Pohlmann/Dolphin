using System.CommandLine;
using Dolphin;
using Dolphin.Cli;
using Dolphin.Lsp;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin lsp`           → LSP server mode (Claude Code plugin uses this)
// `dolphin check`         → CLI analysis mode

Environment.Exit(await Startup.RunAsync(args));

namespace Dolphin
{
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
}
