---
name: grill-heuristic-report
description: Grill sub-skill. When a task involves any heuristic algorithm, confirm the agent reports the heuristic before implementing. Activate during grill sessions touching algorithms, tuning, or approximation.
---

# Grill: Heuristic Report

Any heuristic algorithm must be **reported before implementing**.

During grill, if the task involves a heuristic (approximation, tuning, magic constants, trial-and-error parameters), the agent must:
1. Name the heuristic explicitly
2. State why a heuristic is necessary vs. a deterministic approach
3. Get acknowledgment before proceeding

This prevents silent heuristic injection disguised as exact solutions.
