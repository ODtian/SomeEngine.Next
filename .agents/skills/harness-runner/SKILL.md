---
name: harness-runner
description: Run the SomeEngine harness gate. Use whenever Codex must validate a run or batch by invoking the single harness entry, applying fixed hard/warning policy, and interpreting PASS, NEEDS_FIX, NEEDS_GRILL, or HARNESS_BROKEN.
---

# Harness Runner

Use this as a helper, not as a planning or implementation workflow.

## Command

Run:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File harness/RunHarness.ps1 -RunId <id>
```

For local hard-only validation after a build:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File harness/RunHarness.ps1 -Mode Hard -RunId <id> -NoBuild
```

## Responsibilities

- Run the repository-defined harness entry.
- Preserve the fixed hard/warning policy implemented by `harness/RunHarness.ps1`.
- Report the resulting status.
- Treat warning failures as warnings, not blocking failures.

## Do not do

- Do not design harness.
- Do not edit product or harness code.
- Do not execute review targets or write review results.
- Do not choose hard/warning classification per run.
- Do not call `just all` as the batch completion gate; use `harness/RunHarness.ps1`.

## Status meanings

```text
PASS            hard checks and ReviewTargetGate passed; warnings may exist
NEEDS_FIX       code, tests, harness artifacts, or review results need repair
NEEDS_GRILL     accepted intent/harness conflicts and must be re-grilled
HARNESS_BROKEN  runner/gate/result schema is invalid or cannot run reliably
```

If a run id is supplied, the script writes:

```text
.agent-runs/<id>/batch/harness-run.json
```
