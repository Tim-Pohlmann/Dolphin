using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Lsp;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin lsp`           → LSP server mode (Claude Code plugin uses this)
// `dolphin check`         → CLI analysis mode

if (args is ["serve", "--stdio"])
{
    await McpServer.RunAsync();
    return;
}

if (args is ["lsp"])
{
    await LspServer.RunAsync();
    return;
}

var root = new RootCommand("Dolphin — custom static analysis powered by Opengrep")
{
    CheckCommand.Build()
};

root.Name = "dolphin";
await root.InvokeAsync(args);
