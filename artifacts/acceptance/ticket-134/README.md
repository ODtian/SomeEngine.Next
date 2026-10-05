# #134 acceptance record index

See `wiki/architecture/validation/Vsm-Window-Work-134.md` for exact commands, source/content provenance, discovery/result counts, source obligations and acceptance limits.

Production source is `b82a9dc9e5dba88394b2b20b84cdae93cb1b042c`; main source/shader checkpoint is `eef0934596d9f2c13918a9d8b6842b514404b273`. The follow-up only declares atomic work-status writes partial. `version.json` identifies source/content hashes. No RT numerical/performance acceptance is claimed.

## Accepted concentrated records

- `public-rendering.trx` / `.log`: 30 discovered, 15 executed/passed, 0 failed, 15 Vulkan Presentation capability-skips.
- `work-and-alpha-green.trx` / `.log`: 9 discovered/executed/passed, 0 failed/skipped (6 headless rendering + 3 public output).
- `material-routing.trx` / `.log`: 8 discovered/executed/passed, 0 failed/skipped.
- `render-graph.trx` / `.log`: 178 discovered/executed/passed, 0 failed/skipped.
- `final-work-status.trx` / `.log`: final-source repeated work-memory cases, 2 discovered/executed/passed.
- `final-pressure.trx` / `.log`: final-source repeated surface-pressure cases, 2 discovered, 1 executed/passed, 1 Vulkan Presentation capability-skip.
- `surface-pressure-red.trx` / `.log`: actual base-production red, 2 discovered, 1 executed/failed (expected Rendering, actual None), 1 capability-skip.
- `surface-pressure-green.trx` / `.log`: first tessellation-status green, 2 discovered, 1 executed/passed, 1 capability-skip. Not a separate unique final case.
- `content-build.log`: source-compatible default generation, 69 assets.
- `default-game.log`: 12 default 1024-sphere frames with TAA/VSM/three queues. Startup only, no convergence or throughput measurement.
- `cluster-release-build.log`, `runtime-release-build.log`: final source, 0 warnings/errors.
- `test-rules.log`, `rules/test-rules.log`: final isolated checker, exit 0, 23 projects, 0 violations.
- `dotnet-info.log`, `version.json`: toolchain and checked hashes.

## Explicitly superseded / non-acceptance history

The tool guard denied removal of these own files because it classified them as acceptance evidence. They are retained, not promoted to final acceptance:

- `work-memory.trx` / `.log`: initial point-scene settled-only check, 2 passes, but cached zero target work; superseded by sparse-receiver first/settled `work-and-alpha-green` evidence.
- `work-and-alpha.trx` / `.log`: initial fixture-alpha setup failure, 9 discovered/executed, 5 passed and 4 failed. The fixture had hard-coded texture alpha 255 instead of the requested value. The corrected fixture passed the final 9-case rerun; production renderer was not changed for this setup error.

Do not add repeated/superseded observations to unique acceptance totals, count skips as passes, or treat image parity as aligned full-receiver ray-tracing truth. No source mutation was performed.
