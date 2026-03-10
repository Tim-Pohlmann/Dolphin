using Dolphin;

// `dolphin serve --stdio` → MCP server mode (Claude Code uses this)
// `dolphin lsp`           → LSP server mode (Claude Code plugin uses this)
// `dolphin check`         → CLI analysis mode

Environment.Exit(await Startup.RunAsync(args));
