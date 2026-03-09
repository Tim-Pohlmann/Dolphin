using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Lsp;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin lsp`           → LSP server mode (Claude Code plugin uses this)
// `dolphin check`         → CLI analysis mode

await Startup.RunAsync(args);

internal static class Startup
{
    internal static async Task RunAsync(string[] args, Stream? inputStream = null, Stream? outputStream = null)
    {
        if (args is ["serve", "--stdio"])
        {
            await McpServer.RunAsync();
            return;
        }

        if (args is ["lsp"])
        {
            await LspServer.RunAsync(inputStream, outputStream);
            return;
        }

        var root = new RootCommand("Dolphin — custom static analysis powered by Opengrep")
        {
            CheckCommand.Build()
        };

        root.Name = "dolphin";
        await root.InvokeAsync(args);
    }
}
