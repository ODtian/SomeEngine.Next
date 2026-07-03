# SomeEngine Agent Entry

## Four-Step Flow

1. Grill the requested outcome until the requirement, boundary, and acceptance criteria are explicit.
2. Implement the harness from the accepted grill clarification before product implementation.
3. Let the agent implement code from the accepted clarification and harness, looping on harness failures until all required checks pass or the requirement must be re-grilled.
4. Summarize only durable, reusable conclusions into the Obsidian wiki; keep raw grill Q&A as grill artifacts.

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
