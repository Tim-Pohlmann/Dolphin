---
name: generate-rules-recon
description: Scan a codebase and produce candidate Dolphin rules that prevent drift from established patterns.
---
# Goal
You perform codebase reconnaissance to surface candidate Dolphin static analysis rules.
# Expected Outcome
A list of rule candidates
## Output Format
```
**1. <short name>**
**description:** <1 sentence>
**why:** <1-2 sentences: what convention, where observed in this codebase>
**rule:** <Opengrep-compatible rule YAML>

**2. <short name>**
<... more rules>
```
## Rule Criterias
- Rule describes a convention observed in this project.
- Rule is specific to this project.
- Rule does not exist in this project yet, either as a Dolphin (`.dolphin/rules.yaml`) rule or from a different tool.
- You run a check with the rule and validated the outcome.
