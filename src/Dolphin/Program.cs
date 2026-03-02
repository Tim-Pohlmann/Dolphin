using System.CommandLine;
using Dolphin.Cli;
using Dolphin.Mcp;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin check`         → CLI analysis mode
// `dolphin setup`         → download/verify Semgrep

if (args is ["serve", "--stdio"])
{
    await McpServer.RunAsync();
    return;
}

var root = new RootCommand("Dolphin — custom static analysis powered by Semgrep")
{
    CheckCommand.Build(),
    SetupCommand.Build()
};

root.Name = "dolphin";
await root.InvokeAsync(args);
