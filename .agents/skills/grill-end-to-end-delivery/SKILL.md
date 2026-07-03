---
name: grill-end-to-end-delivery
description: Grill sub-skill. When clarifying a task, confirm the agent commits to end-to-end delivery, not partial handoff. Activate during grill sessions that scope a feature or fix.
---

# Grill: End-to-End Delivery

During grill, confirm the requested outcome is treated as end-to-end delivery, not a partial step to be handed off.

If the task cannot be completed end-to-end in one loop, surface that as a blocker and re-scope the grill — do not silently produce a partial result.

This is a process constraint, not an automated check. It lives here rather than in AGENTS.md or harness because it cannot be executed mechanically.
