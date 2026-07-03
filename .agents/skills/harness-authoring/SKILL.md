---
name: harness-authoring
description: Run SomeEngine agent Step 2. Use after preflight with a numeric run id to read .agent-runs/<id>/intent.md and harness.md, implement executable harness/tests, create concrete review targets, and write batch/instructions.md before product implementation.
---

# Harness Authoring

Run only Step 2 of the SomeEngine agent flow.

## Inputs

Read:

```text
.agent-runs/<id>/intent.md
.agent-runs/<id>/harness.md
```

`harness.md` is input describing how the intent should be harnessed. It is not a runner manifest.

## Outputs

Produce:

```text
harness/...                         stable harness code/checks
tests/...                           product/API/behavior tests when appropriate
.agent-runs/<id>/batch/instructions.md
.agent-runs/<id>/batch/review-targets/*.md
```

Create `review-results/` only as an empty directory if needed; do not write results.

## Rules

- Do not implement product code.
- Do not enter the batch loop.
- Do not write final report/status.
- Convert `Hard Requirements` into tests, API shape checks, architecture gates, or stable harness code when mechanically reliable.
- Convert non-mechanical accepted requirements into specific review targets.
- Keep warning candidates as warning/report only unless a stable policy already makes them hard.
- Do not downgrade stable hard checks to warning.
- Do not put generic runner rules into `batch/instructions.md`.

## Review targets

Each target is a concrete design/review objective, not a category. Good target names:

```text
wiki-maintained-for-agent-flow.md
harness-change-does-not-weaken-contract.md
render-boundary-not-semantically-bypassed.md
migration-has-no-temporary-exceptions.md
```

Each file must state what to review, pass conditions, fail conditions, and when to use `NEEDS_GRILL:`.

## `batch/instructions.md`

Write only run-specific implementation work:

```md
# Batch Instructions

## Objective

## Inputs

## Work Items

## Success Criteria

## Stop Conditions
```

Do not duplicate how to run harness, review result JSON schema, hard/warning policy, or finalization procedure.
