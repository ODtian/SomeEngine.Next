---
name: preflight
description: Run a preflight clarification for a new grill/preflight run: allocate the next numeric .agent-runs/<id>/ directory, explicitly use the grilling skill for the intent grill, and write grill.md, intent.md, and harness.md before harness authoring.
---

# Preflight

Run only the preflight clarification stage.

## Responsibilities

1. Allocate the next run id by scanning `.agent-runs/` numeric directories and creating `.agent-runs/<id>/` with a four-digit id such as `0001`.
2. Use the `grilling` skill to grill the user only for intent: goal, scope, boundaries, unacceptable outcomes, durable wiki candidates, and re-grill triggers.
3. After the intent grill is complete, write only the final raw transcript to `.agent-runs/<id>/grill.md`.
   - Do not create or update `grill.md` while the grill is still active.
   - Do not pre-fill `grill.md` before the user has answered every grill question needed for the accepted intent.
   - `Q` entries are the agent's questions to the user. `A` entries are the user's answers, copied as the source material.
   - Do not include research notes, context summaries, recommendations, assistant explanations, decisions, interpretations, or open-branch notes in `grill.md`.
4. Write user intent to `.agent-runs/<id>/intent.md`.
5. Write harness input to `.agent-runs/<id>/harness.md`.
6. End by reporting the run id and the next command: `harness-authoring <id>`.

## Do not do

- Do not implement product code.
- Do not implement harness code.
- Do not create review results.
- Do not enter the batch implementation loop.
- Do not use `.agent-runs/current`, `latest`, timestamp slugs, or batch subnumbers.
- Do not ask the user how to implement tests, analyzers, review JSON, or runner commands.
- Do not create `.agent-runs/<id>/grill.md` until the preflight intent grill is complete.
- Do not use `.agent-runs/<id>/grill.md` for prototype-only work unless the user explicitly requested a preflight run.

## `intent.md` format

```md
# Intent

## Goal

## In Scope

## Out of Scope

## Must Hold

## Must Not Happen

## Acceptance in User Terms

## Wiki Impact Candidates

## Re-Grill Triggers
```

## `harness.md` format

`harness.md` is Step 2 input, not a command list.

```md
# Harness Input

## Hard Requirements

## Review Requirements

## Warning Candidates

## Out of Scope

## Re-Grill Triggers
```

## Grill boundary

Ask the user only about semantic conflicts, scope conflicts, irreversible tradeoffs, product behavior choices, or design-fit concerns. For ordinary implementation uncertainty, decide during harness authoring instead of asking the user.
