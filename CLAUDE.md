# Dolphin

Custom static code analysis powered by [Opengrep](https://opengrep.dev), distributed as a Claude Code plugin.

## Architecture

Two components:

- **`src/Dolphin/` (.NET)** — the core tool. `Program.cs` pattern-matches `["serve", "--stdio"]` to enter MCP server mode; everything else goes to the `check` CLI. Scanner resolution tries the bundled binary first, then `opengrep`/`semgrep` on PATH.
- **`launcher/launcher.js` (Node.js)** — runs on first plugin install; downloads the platform-specific .NET binary from GitHub Releases and caches it, then execs it. The version in `.claude-plugin/plugin.json` drives which release is fetched.

Supporting:
- `skills/generate-rules/` — Claude Code skill for interactive rule generation
- `agents/generate-rules-recon.md` — subagent used internally by the skill for codebase recon
- `.dolphin/rules.yaml` — this project's own Dolphin rules

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

## Key conventions

- **Scanner binary**: Opengrep is bundled as `opengrep`/`opengrep.exe` at publish time via the `BundleOpengrep` MSBuild target. For dev/source runs, `opengrep` (then `semgrep` as fallback) is resolved from PATH.
- **Rules file**: `.dolphin/rules.yaml` in the scanned project root. Must contain only ASCII characters — Opengrep's Python layer reads it as ASCII.
- **Exit codes**: `0` = no ERROR findings, `1` = at least one ERROR finding, `2` = fatal error.
- **Trimmed publish**: `PublishTrimmed=true` — use source-generated JSON (`[JsonSerializable]`) and avoid reflection-based serialization.
- **Integration tests**: tests that invoke the scanner call `Assert.Inconclusive` when no scanner is found, so they appear inconclusive rather than failing in environments without Opengrep on PATH.
