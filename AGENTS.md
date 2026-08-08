# SomeEngine Agent Entry

## Default Workflow

The previous preflight → harness-authoring → batch-workstream → harness-runner workflow is suspended. Do not enter it by default.

The only project-specific workflow available by default is one Grill:

1. Use `grilling` when the user explicitly asks to grill/stress-test a plan or when a design genuinely requires interactive clarification.
2. Use `grill-with-docs` instead only when the user explicitly wants the grill to maintain ADRs, glossary, or other durable design documentation.
3. Ask one question at a time. Inspect the repository instead of asking questions whose answers can be discovered locally.
4. Once the Grill is accepted, stop the project-specific workflow. Any requested implementation proceeds as ordinary repository work with proportionate build/tests, not through preflight, harness authoring, batch workstreams, numeric runs, or a mandatory single-harness gate.

Do not create `.agent-runs/`, grill transcript artifacts, harness code, review targets, batch instructions, or harness status reports merely because work is being implemented. Existing harness files may still be run or edited when the user explicitly requests them, but they are not a default workflow or completion requirement.

Default builds and tests use `SomeEngine.slnx`, which excludes harness projects. The remaining harness checks are ordinary opt-in .NET tests, available only through `SomeEngine.Harness.slnx` or `just harness-test`. There is no PowerShell harness runner, hard/warning bucket, run-id protocol, or alternate scripted test orchestration.

## Temporarily Disabled Skills

- The only user-facing skills enabled by default are `grilling` and `grill-with-docs`.
- All other repository, global, plugin, and project skills are temporarily disabled, even when their descriptions appear to match the task. Do not invoke them unless the user explicitly names or re-enables the skill in a later request.
- `domain-modeling` is not independently enabled. It may be used only as the internal dependency required by an explicitly requested `grill-with-docs` session.
- In particular, do not automatically invoke `preflight`, `harness-authoring`, `harness-runner`, `batch-workstream`, `batch-workstream-strict`, any `grill-*` sub-skill, or `grill-session-record`.
- Do not delete disabled skill files. This is a temporary invocation policy, not removal of the installed skills.

## Project Structure

- `src/` contains product code.
- `tests/` contains executable tests.
- `tools/` contains project tools and validation executables.
- `assets/` contains shaders, schemas, and project assets.
- `external/` contains third-party dependencies.
- `harness/` contains automatic checks, baselines, schemas, and run artifacts.
  - `harness/maintainability/` — code quality, runtime metrics, wiki link gate.
  - `harness/architecture/` — layer dependency, profiler禁区, API shape.
  - `harness/behaviour/` — functional tests, diff intent cross-validation.
- `wiki/` is an Obsidian vault for durable project knowledge.
- `.agents/skills/` contains grill-sub skills forked from the global skill vault.
