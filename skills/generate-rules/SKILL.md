---
name: generate-rules
description: Scan this codebase for established patterns and generate Dolphin rules that prevent drift from those conventions
argument-hint: "[focus-area] (optional — e.g. 'logging', 'error-handling', 'naming', or 'api-responses')"
allowed-tools: Agent(generate-rules-recon), Bash(mkdir *), Bash(test *), Read, Write
---

You are orchestrating static analysis rule generation for the Dolphin tool.
Goal: **drift prevention** — encode patterns the team already uses so future code stays aligned.
User's optional focus area: $ARGUMENTS

---

## PHASE 1 — RECON

Use the Read tool to read `.dolphin/rules.yaml` (if it exists) and collect existing rule IDs to skip. If the file does not exist, proceed without any IDs to skip.

Invoke the `generate-rules-recon` agent:

> "Scan for project-specific conventions to protect against drift. Focus area: $ARGUMENTS (if empty, cover the most common drift vectors). Existing rule IDs to skip: <IDs from .dolphin/rules.yaml above>."

Parse the returned `CANDIDATE_RULES` entries: `id`, `severity`, `languages`, `pattern`, `message`, `why`.

---

## PHASE 2 — INTERACTIVE REFINEMENT

Propose rules **one at a time**:

```
Rule N: <id>
  Severity:  ERROR | WARNING | INFO
  Languages: <list>
  Pattern:   <pattern>
  Message:   <message>
  Why:       <why this is specific to this codebase>

Keep (k), Skip (s), or Modify (m)?
```

- **k** → add to confirmed list, next rule
- **s** → discard, next rule
- **m** → ask what to change, re-show, re-ask

After all rules, show summary and ask:

> "Confirmed N rule(s): [id-1], [id-2], ... — write to `.dolphin/rules.yaml`? (yes/no)"

---

## PHASE 3 — WRITE

1. Check for existing file using `test -f .dolphin/rules.yaml`. If it exists, read it with the Read tool and **append** confirmed rules to the existing `rules:` list rather than overwriting. Warn if any confirmed ID already exists in the file (duplicate).

2. Ensure directory:
   ```bash
   mkdir -p .dolphin
   ```

3. Build YAML. Each rule must use **exactly one** of `pattern`, `patterns`, `pattern-either`, or `pattern-regex`:
   ```yaml
   rules:
     - id: <kebab-case-id>
       message: "<message>"
       languages: [<lang1>, <lang2>]
       severity: ERROR | WARNING | INFO
       # Use ONE of:
       pattern: <single pattern>
       # patterns: [list of patterns with pattern/pattern-not/etc.]
       # pattern-either: [list of alternatives]
       # pattern-regex: <regex string>
   ```
   Tips: `...` for any args, `$VAR` for metavariables.

4. Write the file.

5. Confirm:
   > "Written N rule(s) to `.dolphin/rules.yaml`. Run `dolphin check` to validate."
