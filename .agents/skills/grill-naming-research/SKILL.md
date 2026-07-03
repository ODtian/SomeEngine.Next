---
name: grill-naming-research
description: Grill sub-skill. All class and method names must be researched on GitHub before adoption. Blacklist suffixes (XXPlan, XXRun, XXProgram) are enforced mechanically by harness; the research step is enforced here. Activate during grill sessions that introduce new types.
---

# Grill: Naming Research

All class and method names must be **researched on GitHub** before adoption.

Process:
1. Search GitHub for the proposed name
2. Check if it conflicts with established patterns or has unwanted connotations
3. Confirm the name is specific, not generic

Mechanically enforced blacklist (in harness Roslyn analyzer):
- `*Plan` — vague, implies deferred execution
- `*Run` — vague, implies a process wrapper
- `*Program` — reserved for entry points only

The GitHub research step cannot be automated; it lives here as a grill gate.
