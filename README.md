# Dolphin

Custom static code analysis powered by [Opengrep](https://opengrep.dev), integrated with Claude Code.

## AI Slop
This repo is full of AI Slop.

## What it does

- **Generate rules** — Install the Dolphin plugin in Claude Code and use the `generate-rules` skill. Claude analyzes your codebase, proposes rules one by one, lets you keep/skip/modify each, then writes `.dolphin/rules.yaml`.
- **Run rules** — Use `dolphin check` in your terminal or CI pipeline. No Claude required at runtime.

## Installation

### As a Claude Code plugin

```
/plugin marketplace add <marketplace-url>
```

Then install the `dolphin` plugin. The plugin provides:
- The `generate-rules` skill for interactive rule generation
- An MCP server that Claude uses to run checks during conversations
- The `dolphin` CLI binary

### Manual CLI install

Download the binary for your platform from [Releases](../../releases), make it executable, and add to your `PATH`:

```bash
chmod +x dolphin
sudo mv dolphin /usr/local/bin/
```

## Usage

### Generate rules (in Claude Code)

Invoke the `generate-rules` skill. Claude will:
1. Analyze your codebase
2. Propose rules one by one — you `keep`, `skip`, or `modify` each
3. Write the confirmed rules to `.dolphin/rules.yaml`

### Run analysis

```bash
# Run all rules
dolphin check

# Run a specific rule
dolphin check --rule no-console-log

# JSON output for CI
dolphin check --format json

# Scan a specific directory
dolphin check --cwd /path/to/project
```

### Exit codes

| Code | Meaning |
|------|---------|
| `0`  | No `ERROR`-severity violations |
| `1`  | One or more `ERROR`-severity violations found |
| `2`  | Fatal error (scanner unavailable, rules file missing, etc.) |

### Editor integration (LSP)

Dolphin ships a Language Server Protocol server for `.dolphin/rules.yaml` files, giving you diagnostics, completions, and hover docs while you write rules.

```bash
dolphin lsp --stdio
```

**Features**
- **Diagnostics** — highlights missing required fields (`id`, `message`, `languages`, `severity`, pattern), invalid severity values, unknown language identifiers, and duplicate rule IDs
- **Completions** — field names, `ERROR`/`WARNING`/`INFO` after `severity:`, language identifiers after `languages:`, pattern operators inside `patterns:`
- **Hover** — inline documentation for every rule field

**VS Code** — add to `.vscode/settings.json`:

```json
{
  "yaml.customTags": [],
  "[yaml]": {},
  "dolphin.lsp.enabled": true
}
```

Then configure your YAML LSP extension (e.g. `vscode-yaml`) or install a generic LSP client (`vscode-glspc`) and point it at `dolphin lsp --stdio`.

**Neovim** (with `nvim-lspconfig`):

```lua
local configs = require('lspconfig.configs')
configs.dolphin = {
  default_config = {
    cmd = { 'dolphin', 'lsp', '--stdio' },
    filetypes = { 'yaml' },
    root_dir = require('lspconfig.util').root_pattern('.dolphin'),
  },
}
require('lspconfig').dolphin.setup({})
```

**Zed** — add to your `settings.json`:

```json
{
  "lsp": {
    "dolphin": {
      "binary": { "path": "dolphin", "arguments": ["lsp", "--stdio"] }
    }
  }
}
```

## Rule format

Rules are stored in `.dolphin/rules.yaml` using the Opengrep rule schema:

```yaml
rules:
  - id: no-console-log
    message: "Remove console.log before committing."
    languages: [typescript, javascript]
    severity: WARNING
    patterns:
      - pattern: console.log(...)
```

Commit `.dolphin/rules.yaml` to your repository so the whole team uses the same rules.

## Development

```bash
# Build
dotnet build

# Run tests
dotnet test

# Publish self-contained binary
dotnet publish src/Dolphin -r linux-x64 -c Release -o bin/
dotnet publish src/Dolphin -r osx-arm64 -c Release -o bin/
```
