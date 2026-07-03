---
name: preflight
description: Run SomeEngine agent Step 1. Use when starting a new grill/preflight run to clarify user intent, allocate the next numeric .agent-runs/<id>/ directory, and write grill.md, intent.md, and harness.md as Step 2 input before harness authoring.
---

# Preflight

Run only Step 1 of the SomeEngine agent flow.

## Responsibilities

1. Allocate the next run id by scanning `.agent-runs/` numeric directories and creating `.agent-runs/<id>/` with a four-digit id such as `0001`.
2. Grill the user only for intent: goal, scope, boundaries, unacceptable outcomes, durable wiki candidates, and re-grill triggers.
3. Write raw Q&A to `.agent-runs/<id>/grill.md`.
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
