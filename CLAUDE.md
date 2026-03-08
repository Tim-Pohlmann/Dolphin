# Dolphin

Custom static code analysis powered by [Opengrep](https://opengrep.dev), distributed as a Claude Code plugin.

## Architecture

```
src/Dolphin/
  Program.cs               Entry point: routes to MCP server or CLI
  Cli/CheckCommand.cs      `dolphin check` command
  Scanner/Installer.cs     Locates the Opengrep binary (bundled or PATH)
  Scanner/Runner.cs        Invokes Opengrep, parses JSON output
  Scanner/Models.cs        Finding, RunResult, Severity
  Mcp/Server.cs            MCP server host
  Mcp/Tools/RunCheckTool.cs  MCP tool: run_check
  Output/Formatter.cs      Text and JSON output formatting

tests/Dolphin.Tests/
  CheckCommandTests.cs     Tests for the `dolphin check` CLI command
  InstallerTests.cs        Tests for binary resolution
  McpProtocolTests.cs      Tests for MCP server JSON-RPC protocol
  RunCheckToolTests.cs     Tests for the run_check MCP tool
  RunnerTests.cs           Integration tests (skipped if no scanner on PATH)
  TestProcessHelper.cs     Shared helpers for tests that spawn Dolphin as a child process
  fixtures/
    rules.yaml             Sample rules for tests
    sample-src/
      bad-file.ts          Source file that triggers test rules
      clean-file.ts        Source file with no violations

launcher/
  launcher.js              Node.js launcher: downloads platform-specific binary from GitHub Releases
  launcher.test.js         Tests for the launcher

agents/
  generate-rules-recon.md  Subagent for codebase reconnaissance (used by generate-rules skill)

skills/generate-rules/     Claude Code skill for interactive rule generation
.claude-plugin/plugin.json Plugin metadata
.mcp.json                  MCP server config (used when plugin is installed)
.dolphin/rules.yaml        This project's own Dolphin rules
```

## Commands

```bash
# Build
dotnet build

# Test
dotnet test

# Run CLI against this repo
dotnet run --project src/Dolphin -- check --cwd .
dotnet run --project src/Dolphin -- check --cwd . --format json

# Publish self-contained binaries
dotnet publish src/Dolphin -r linux-x64   -c Release -o bin/
dotnet publish src/Dolphin -r linux-arm64 -c Release -o bin/
dotnet publish src/Dolphin -r osx-arm64   -c Release -o bin/
dotnet publish src/Dolphin -r win-x64     -c Release -o bin/

# Test the Node.js launcher
node --test launcher/launcher.test.js
```

## Key conventions

- **Scanner binary**: Opengrep is bundled as `opengrep`/`opengrep.exe` at publish time via the `BundleOpengrep` MSBuild target. For dev/source runs, `opengrep` (then `semgrep` as fallback) is resolved from PATH.
- **Rules file**: `.dolphin/rules.yaml` in the scanned project root. Must contain only ASCII characters — Opengrep's Python layer reads it as ASCII.
- **Exit codes**: `0` = no ERROR findings, `1` = at least one ERROR finding, `2` = fatal error.
- **Trimmed publish**: `PublishTrimmed=true` — use source-generated JSON (`[JsonSerializable]`) and avoid reflection-based serialization.
- **MCP server**: `dolphin serve --stdio` — no args other than that exact pair; matched by pattern in `Program.cs`.
