# Dolphin

Custom static code analysis powered by [Opengrep](https://opengrep.dev), integrated with Claude Code.

## AI Slop Disclaimer 
This repo is a vibe coded side project and therefore full of AI Slop.

## Why?

You have a mature codebase with a defined architecture and coding style and you don't want AI agents to ruin it? Dolphin will slap AI agents (and sloppy developers) in the face and force them to follow **your** rules!

## What?
Dolphin helps you generate and enforce Opengrep rules. It comes in three major components:
- **Dolphin CLI**: Enforces your rules. Intended to be used in CI or for manual checks.
- **LSP**: Serves diagnostics based on your rules in real-time to your AI agent or IDE.
- **Claude Code plugin**:
  - Provides a skill to help with generating rules.
  - Serves the LSP to Claude and fights the slop directly at the root.
  - Wraps the CLI check in a nest MCP tool.
 
## How?

### Claude Code 

```
/plugin marketplace add Tim-Pohlmann/Dolphin
/plugin install Dolphin@Dolphin
/generate-rules 
```
The LSP and MCP will be enabled automatically.

### CLI

Download the binary from [Releases](../../releases) and run it:
```
dolphin check
```
It will run all rules and print out a report. If any violation with severity `ERROR` is found, the tool emits exit code 1, which can be used to block your CI.
