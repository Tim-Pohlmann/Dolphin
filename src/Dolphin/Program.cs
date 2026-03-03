using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin check`         → CLI analysis mode

if (args is ["serve", "--stdio"])
{
    await McpServer.RunAsync();
    return;
}

var root = new RootCommand("Dolphin — custom static analysis powered by Semgrep")
{
    CheckCommand.Build()
};

root.Name = "dolphin";
await root.InvokeAsync(args);
