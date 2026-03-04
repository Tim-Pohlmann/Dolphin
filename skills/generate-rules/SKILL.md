---
name: generate-rules
description: Analyze this codebase and generate static analysis rules for .dolphin/rules.yaml
argument-hint: "[focus-area] (optional — e.g. 'security', 'style', or 'performance')"
disable-model-invocation: true
allowed-tools: Bash(ls *), Bash(head *), Bash(mkdir *), Bash(test *), Glob, Grep, Read, Write
---

You are generating static analysis rules for the Dolphin tool.
These rules will be written to `.dolphin/rules.yaml` and executed later by the
`dolphin check` CLI command — WITHOUT Claude. Every rule must be a valid Opengrep
rule that Opengrep can execute independently.

The user's optional focus area is: $ARGUMENTS
If no focus area was provided, perform a broad analysis covering security, style, and correctness.

---

## PHASE 1 — CODEBASE RECONNAISSANCE

Work through these steps before proposing any rules.

**Step 1.1 — Discover the project layout**
Use Glob with `**/*` and Bash `ls` to understand:
- Languages present (check extensions: .ts, .js, .py, .go, .java, .cs, .rb, etc.)
- Directory structure (src/, lib/, app/, tests/, etc.)
- Config files present (package.json, pyproject.toml, go.mod, .eslintrc, etc.)

**Step 1.2 — Sample source files**
Use Glob to find up to 20 representative source files across major directories.
Use Read (or Bash `head -n 80`) to read samples. Look for:
- Logging patterns (console.log, print, fmt.Println, logger.*)
- TODO/FIXME comment patterns
- Import/require conventions
- Error handling patterns
- Potential hardcoded secrets or API keys
- Deprecated API usage
- Naming convention inconsistencies

**Step 1.3 — Check existing lint configurations**
Read `.eslintrc*`, `pyproject.toml`, `.rubocop.yml`, etc. if present.
Do NOT propose rules already enforced by existing linters.

**Step 1.4 — Note language and file scope**
Record which languages and directories you'll target per rule. Use precise
`languages` and `paths.include` fields rather than broad wildcards.

---

## PHASE 2 — INTERACTIVE RULE REFINEMENT

Propose rules **one at a time**. For each rule, present:

```
Rule N: <rule-id>
  Severity:  ERROR | WARNING | INFO
  Languages: <comma-separated list>
  Pattern:   <the pattern>
  Message:   <violation message shown to the developer>
  Why:       <1-2 sentences on why this is relevant to THIS codebase>

Keep (k), Skip (s), or Modify (m)?
```

Wait for the user's response before moving to the next rule:
- **k / keep** → add to confirmed list, show next rule
- **s / skip** → discard, show next rule
- **m / modify** → ask what to change, apply changes, re-show updated rule, re-ask

Target 8–15 candidate rules total. After all rules are reviewed, show a summary:

> "You confirmed N rule(s): [rule-id-1], [rule-id-2], ...
> Ready to write `.dolphin/rules.yaml`? (yes/no)"

Wait for confirmation before writing.

---

## PHASE 3 — WRITE THE RULES FILE

Once the user confirms:

1. **Check for existing file:**
   ```bash
   test -f .dolphin/rules.yaml && echo exists || echo missing
   ```
   If it exists, warn: "`.dolphin/rules.yaml` already exists — writing will OVERWRITE all existing rules. Confirm? (yes/no)"
   Wait for confirmation before overwriting.

2. **Ensure directory exists:**
   ```bash
   mkdir -p .dolphin
   ```

3. **Build the YAML** for all confirmed rules. Each rule must follow this Opengrep schema:
   ```yaml
   rules:
     - id: <kebab-case-id>
       message: "<violation message>"
       languages: [<lang1>, <lang2>]
       severity: ERROR | WARNING | INFO
       # Use ONE of: pattern, patterns, pattern-either, pattern-regex
       pattern: |
         <pattern with $METAVARIABLES and ... ellipsis>
   ```

   Pattern tips:
   - Use `...` to match any arguments: `console.log(...)`
   - Use `$VAR` to capture expressions: `$KEY = "$VALUE"`
   - Use `pattern-regex` for regex-based matching when AST patterns aren't suitable
   - Use `pattern-either` to match multiple alternatives
   - Always test that the pattern is valid Opengrep syntax

4. **Write the file** using the Write tool.

5. **Confirm success:**
   > "Written N rule(s) to `.dolphin/rules.yaml`.
   > Run `dolphin check` to run your first scan."
