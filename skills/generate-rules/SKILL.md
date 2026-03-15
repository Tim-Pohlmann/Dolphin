---
name: generate-rules
description: Scan this codebase for established patterns and generate Dolphin rules that prevent drift from those conventions
argument-hint: "[focus-area] (optional — e.g. 'logging', 'error-handling', 'naming', or 'api-responses')"
allowed-tools: Agent(generate-rules-recon), Bash(mkdir *), Bash(test *), Write
---

You are orchestrating static analysis rule generation for the Dolphin tool.
The goal is **drift prevention**: encode the patterns the team already uses consistently so that future code stays aligned with those conventions.
The user's optional focus area is: $ARGUMENTS

---

## PHASE 1 — CODEBASE RECONNAISSANCE (delegated)

Invoke the `generate-rules-recon` agent with the following prompt:

> "Scan this codebase for established patterns to protect against drift. Focus area: $ARGUMENTS (if empty, cover the most common drift vectors: logging, error handling, naming conventions, API/response patterns, and dependency usage)."

Wait for the agent to return its `RECON_RESULT` block. Parse out the `CANDIDATE_RULES` entries — each has: `id`, `severity`, `languages`, `pattern`, `message`, `why`.

---

## PHASE 2 — INTERACTIVE RULE REFINEMENT

Propose rules **one at a time** from the candidate list. For each rule, present:

```
Rule N: <rule-id>
  Severity:  ERROR | WARNING | INFO
  Languages: <comma-separated list>
  Pattern:   <the pattern>
  Message:   <message shown to the developer>
  Why:       <1-2 sentences on what pattern this enforces in THIS codebase>

Keep (k), Skip (s), or Modify (m)?
```

Wait for the user's response before moving to the next rule:
- **k / keep** → add to confirmed list, show next rule
- **s / skip** → discard, show next rule
- **m / modify** → ask what to change, apply changes, re-show updated rule, re-ask

After all rules are reviewed, show a summary:

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
   > Run `dolphin check` to see drift from your conventions."
