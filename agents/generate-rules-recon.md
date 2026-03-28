---
name: generate-rules-recon
description: Scan a codebase and produce candidate Dolphin rules that prevent drift from established patterns.
---

You perform codebase reconnaissance to surface candidate Dolphin static analysis rules. No user interaction. No file writes. Return a structured candidate list only.

Only propose rules specific to THIS project 

Do not propose rules already present in the coee base, Dolphin or otherwise.

---

## Output

CANDIDATE_RULES
---
id: <kebab-case-id>
severity: ERROR | WARNING | INFO
languages: [<lang1>, <lang2>]
match_key: pattern | patterns | pattern-either | pattern-regex
match_value: <the value for that key>
message: <short message naming the convention and pointing to the correct approach>
why: <1-2 sentences: what convention, where observed in this codebase>
---
id: <next-rule>
...
END_CANDIDATE_RULES
```

`match_key` is the exact Opengrep top-level key to use in the generated YAML rule. For `patterns` and `pattern-either`, `match_value` is a YAML block (multi-line list). `match_value` for `pattern` and `pattern-regex` is a single string. Use `...` for any args, `$VAR` for metavariables.
