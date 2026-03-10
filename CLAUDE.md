# Dolphin

Custom static code analysis powered by [Opengrep](https://opengrep.dev), distributed as a Claude Code plugin.

## Architecture

```
src/Dolphin/
  Program.cs               Entry point: routes to MCP server, LSP server, or CLI
  Cli/CheckCommand.cs      `dolphin check` command
  Scanner/Installer.cs     Locates the Opengrep binary (bundled or PATH)
  Scanner/Runner.cs        Invokes Opengrep, parses JSON output
  Scanner/Models.cs        Finding, RunResult, Severity
  Mcp/Server.cs            MCP server host
  Mcp/Tools/RunCheckTool.cs  MCP tool: run_check
  Output/Formatter.cs      Text and JSON output formatting
  Lsp/LspServer.cs         LSP server: JSON-RPC over stdio; runs `opengrep validate` on YAML files under .dolphin/
  Lsp/LspDiagnosticsParser.cs  Parses `opengrep validate` output into LSP diagnostics

launcher/
  launcher.js              Downloads/caches the dolphin+opengrep binary from GitHub Releases

tests/Dolphin.Tests/
  InstallerTests.cs        Tests for binary resolution
  RunnerTests.cs           Integration tests (skipped if no scanner on PATH)
  LspDiagnosticsParserTests.cs  Unit tests for diagnostic parsing
  fixtures/                Sample rules.yaml and bad-file.ts for tests

skills/generate-rules/     Claude Code skill for interactive rule generation
.claude-plugin/plugin.json Plugin metadata (includes lspServers config)
.mcp.json                  MCP server config (used when plugin is installed)
.dolphin/rules.yaml        This project's own Dolphin rules
```

## Commands

```bash
# Build
dotnet build

# Test
dotnet test
node --test launcher/launcher.test.js

# Run CLI against this repo
dotnet run --project src/Dolphin -- check --cwd .
dotnet run --project src/Dolphin -- check --cwd . --format json

# Publish self-contained binaries
dotnet publish src/Dolphin -r linux-x64   -c Release -o bin/
dotnet publish src/Dolphin -r linux-arm64 -c Release -o bin/
dotnet publish src/Dolphin -r osx-arm64   -c Release -o bin/
dotnet publish src/Dolphin -r win-x64     -c Release -o bin/
```

## Quality

Code quality and coverage are tracked via [SonarCloud](https://sonarcloud.io/project/overview?id=Tim-Pohlmann_Dolphin) (public project, no token needed to view results).

SonarCloud integrates as Roslyn analyzers during the CI build, so its issues appear as **compiler warnings in the "Build" step of the GitHub Actions log** — no SonarCloud login required. Look at the `dotnet build` output in CI to see all current issues without needing browser access to sonarcloud.io.

## Key conventions

- **Scanner binary**: Opengrep is bundled as `opengrep`/`opengrep.exe` at publish time via the `BundleOpengrep` MSBuild target. For dev/source runs, `opengrep` (then `semgrep` as fallback) is resolved from PATH.
- **Rules file**: `.dolphin/rules.yaml` in the scanned project root. Must contain only ASCII characters — Opengrep's Python layer reads it as ASCII.
- **Exit codes**: `0` = no ERROR findings, `1` = at least one ERROR finding, `2` = fatal error.
- **Trimmed publish**: `PublishTrimmed=true` — use source-generated JSON (`[JsonSerializable]`) and avoid reflection-based serialization.
- **MCP server**: `dolphin serve --stdio` — no args other than that exact pair; matched by pattern in `Program.cs`.
- **LSP server**: `dolphin lsp` — matched by pattern in `Program.cs`; called by Claude Code via `launcher.js lsp`.
