---
name: test-local-build
description: Deploy a local Dolphin build to the MCP plugin cache so Claude Code picks it up for testing
---

# Knowledge base

- `CLAUDE_PLUGIN_ROOT` points to the plugin cache, not the repo root — even when the marketplace source is a local directory.
- The launcher caches binaries at `$CLAUDE_PLUGIN_ROOT/bin/cache/{version}/{rid}/`. The version comes from `.claude-plugin/plugin.json`.
- The plugin cache root is `~/.claude/plugins/cache/dolphin-dev/dolphin/{version}/`.
- Before publishing, wipe the entire target directory — not just the binary. The launcher uses an atomic rename, so a partially-populated directory causes "Binary missing after concurrent install attempt."
- The launcher checks for the binary on startup. After replacing it, the user must restart Claude Code.
