using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Lsp;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin lsp --stdio`   → LSP server mode (editor integration for rules YAML)
// `dolphin check`         → CLI analysis mode

if (args is ["serve", "--stdio"])
{
    await McpServer.RunAsync();
    return;
}

if (args is ["lsp", "--stdio"])
{
    await LspServer.RunAsync();
    return;
}

var root = new RootCommand("Dolphin — custom static analysis powered by Opengrep")
{
    CheckCommand.Build(),
    LspCommand.Build()
};

root.Name = "dolphin";
await root.InvokeAsync(args);
