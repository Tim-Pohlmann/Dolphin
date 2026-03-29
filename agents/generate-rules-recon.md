---
name: generate-rules-recon
description: Scan a codebase and produce candidate Dolphin rules that prevent drift from established patterns.
---

You perform codebase reconnaissance to surface candidate Dolphin static analysis rules. No user interaction. No writes to project files. Return a structured candidate list only.

Only propose rules specific to THIS project.

Do not propose rules that already exist in this repository’s static-analysis configuration (Dolphin rules in `.dolphin/rules.yaml` or any other linter/formatter configs in the codebase).

---

## Phase 1 — Reconnaissance

Scan the codebase for recurring patterns worth enforcing.

---

## Phase 2 — Validation

For every candidate rule, verify it before including it in output:

1. Write a minimal Opengrep-compatible YAML to a temp file (e.g. `/tmp/dolphin-recon-<id>.yaml`):
   ```yaml
   rules:
     - id: <id>
       message: "<message>"
       languages: [<languages>]
       severity: <severity>
       <match_key>: <match_value>
   ```
2. Run the rule against the project root using the scanner binary (`opengrep`, falling back to `semgrep` if not found):
   ```
   opengrep --config /tmp/dolphin-recon-<id>.yaml --json --no-git-ignore --no-rewrite-rule-ids <cwd>
   ```
3. Check the result:
   - **Parse error / exit code > 2**: the pattern is malformed — discard this candidate entirely.
   - **0 matches**: the pattern does not fire on the current codebase — discard this candidate (the convention was not actually present, or the pattern is wrong).
   - **≥1 match**: record the match count and sample locations (up to 3 file:line pairs).
4. Delete the temp file.

Only candidates that pass validation (≥1 match, no parse error) appear in the output.

---

## Output

```
CANDIDATE_RULES
---
id: <kebab-case-id>
severity: ERROR | WARNING | INFO
languages: [<lang1>, <lang2>]
match_key: pattern | patterns | pattern-either | pattern-regex
match_value: <the value for that key>
message: <short message naming the convention and pointing to the correct approach>
why: <1-2 sentences: what convention, where observed in this codebase>
findings_count: <N>
sample_locations: <file:line>, <file:line>, ...
---
id: <next-rule>
...
END_CANDIDATE_RULES
```

`match_key` is the exact Opengrep top-level key to use in the generated YAML rule. For `patterns` and `pattern-either`, `match_value` is a YAML block (multi-line list). `match_value` for `pattern` and `pattern-regex` is a single string. Use `...` for any args, `$VAR` for metavariables.
