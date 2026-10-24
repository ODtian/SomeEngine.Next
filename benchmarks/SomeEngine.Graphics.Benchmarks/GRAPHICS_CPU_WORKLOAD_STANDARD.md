# Dagor/Enlisted graphics CPU workload standard

Status: production-distribution-derived RenderGraph + D3D12 CPU standard. This is
one deterministic projection family, not a claim to reproduce a captured Enlisted
frame command-for-command.

## Scope

The metric is the steady-frame CPU interval from immediately before
`RenderGraph.BeginFrame` through real D3D12 command-list close and the final
`Queue.Submit` return. GPU completion waits, `Present`, scene simulation,
visibility generation, asset streaming, GPU execution, and pixel cost are outside
the interval. The measurement uses one frame slot; its completion wait and
slot recycle occur before timing starts.

Every command-bearing case records real D3D12 `Draw`, `ExecuteIndirect`,
`Dispatch`, and `CopyBuffer` commands. Render targets are 1920x1080, while the
scissor is 1x1 because this is deliberately a CPU benchmark. A counting backend,
64x64-only resources, or callbacks that do no native command recording are not
performance evidence.

P95 and the other distributions are reported as observations. No timing or allocation threshold determines success.

## Pinned source

The workload is based on Gaijin's Dagor Engine revision
`6ae9529f0fa5405615648e6610a336bdb41de76f` (`dagor_2026_05_02`). Dagor is the
engine used by War Thunder and Enlisted and exposes a D3D12 backend.

The primary production evidence is
`prog/gameLibs/render/daFrameGraph/tests/performance.cpp`. Its comment states that
the statistics were collected from Enlisted at the end of February 2023, and the
test is named `Production-like input`.

Two implementation facts are required to interpret those numbers correctly:

- `BarrierScheduler::SCHEDULE_FRAME_WINDOW` is 2 (even and odd frames).
- `resourceScheduler.cpp` assigns
  `input.timelineSize = SCHEDULE_FRAME_WINDOW * timepoints_per_frame` and places a
  copy of every scheduled resource in each frame of that window.

Consequently, the recorded timeline values 130, 140, 144, and 146 mean 65, 70,
72, and 73 execution timepoints per frame. They do not mean 130-146 passes in one
frame. The benchmark maps the 73-timepoint high watermark to 73 passes as an
explicit projection; it does not claim that the source measured exactly 73 named
Enlisted passes. The 530 lifetime/size samples are cumulative frame-resource
samples across heap schedules; they are not 530 resources in one frame.

## Exact production distributions

The benchmark keeps the source weights verbatim:

- Two-frame timeline: `(130,4), (140,8), (144,4), (146,4)`.
- Lifetime in timepoints:
  `(0,108), (1,120), (2,36), (6,6), (7,6), (9,4), (13,4), (28,4),
  (33,12), (35,8), (37,4), (38,4), (39,8), (41,4), (42,12),
  (43,4), (44,4), (45,4), (55,4), (56,8), (60,8), (62,16),
  (63,4), (66,4), (67,16), (69,8), (70,8), (71,16), (73,8),
  (74,8), (79,6), (80,8), (82,4), (84,4), (85,16), (89,16),
  (91,12), (92,4)`.
- Allocation size in bytes:
  `(1,28), (4,60), (880,20), (12288,20), (131072,12),
  (524288,20), (589824,4), (917504,20), (1245184,40),
  (2228224,36), (2490368,82), (3538944,96), (4915200,16),
  (8847360,76)`.

The Dagor test deliberately evaluates resource counts 25, 50, 75, 100, 125,
150, 175, and 200. No single per-frame resource count is recoverable from the
cumulative histogram, so the source sweep is retained as reference data
instead of inventing an "actual Enlisted resource count".

## Command structure from source

The public renderer source under
`prog/daNetGame/render/world/frameGraphNodes` contains raster passes (depth,
G-buffer, deferred lighting, skies/clouds, water, transparency, post effects and
UI), compute work (VRS, lighting tiles, G-buffer resolve, GI, SSR, AO and temporal
filters), and transfer work (depth/HZB upload and blits). The frame-graph API models
these as pipeline stages; it does not expose a per-node async-queue selection API.
The canonical CPU workload therefore uses one D3D12 graphics queue and one submit.
Moving the same graph to several queues is a separate synchronization stress test,
not this source-distribution projection.

`prog/daNetGame/render/world/dynModelRenderer.cpp` is also explicit about command
shape: `render()` runs a non-packed direct-draw path and a coalesced multidraw path;
the multidraw path sorts/coalesces state ranges and submits each range through
`multiDrawRenderer.render`. Other nodes issue direct/indirect compute dispatches and
copies. Therefore the benchmark must exercise both direct `Draw` and
`ExecuteIndirect`, plus `Dispatch` and `CopyBuffer`.

Static source cannot determine the scene-dependent lengths of `list`,
`multidrawList`, or `drawcallRanges`. It also does not publish a production count
for raster, compute, copy, and control nodes. The benchmark's 24 raster, 17
compute, 6 copy, and 26 control passes, and all native API command counts, are
fixed projection choices. They are reported separately and never called measured
Enlisted counts.

## Deterministic cases

Each case is generated from a checked-in seed and has:

- 73 passes mapped from the source's 73-timepoint high watermark;
- a selected resource count (default 200, minimum 4);
- allocation sizes and lifetimes drawn from the exact weighted distributions above;
- full-HD color targets and a 1x1 scissor;
- one graphics queue and one submission;
- one frame slot, split barriers disabled, and every projected pass eligible for
  coarse worker recording;
- queue-specific common layout for ordinary Graphics-queue UAV/SRV/Copy textures;
  same-layout access transitions at one boundary are represented by one stronger
  Enhanced Global Barrier, while attachment/layout-changing/alias barriers remain
  resource-specific;
- real direct draws, indirect draws, dispatches, and copies, with their benchmark
  counts emitted in the JSON report.

The default workload uses 200 resources and the source projection's pass count.
`--resource-counts` accepts counts of at least 4, including values outside the
original source sweep. These choices describe the workload; no selected size or
timing value is an acceptance condition.

## Interpreting measurements

Measurements describe the selected workload and environment. Compare command
output correctness through functional tests, and report timing separately. There
is no required certification profile, process count, machine configuration, or
performance threshold for retaining a product change.

## Measurement protocol

- Explicit hardware adapter; ordinary process and thread priority.
- Use the machine's current background load at ordinary process and thread
  priority. Do not reject a run for high system load, raise priority, set affinity,
  or stop unrelated workloads to manufacture a pass.
- Warm-up and sample counts are configurable. Plateau status and additional process runs describe measurement variability.
- Managed allocation and GC counts are not acceptance criteria. Any pause that
  lands inside the wall-clock timing boundary remains part of the measured sample.
- Report P50, P95, P99, maximum, command counts, pass/resource/access counts,
  logical barrier count and barrier-boundary count, queue count, submission count,
  and source revision.
- The 200-resource case is the default workload; its timing has no pass/fail budget.

## References

- Dagor Engine repository: <https://github.com/GaijinEntertainment/DagorEngine>
- Dagor Engine overview: <https://gaijinentertainment.github.io/DagorEngine/dagor-home/dagor_engine.html>
- daFrameGraph documentation: <https://gaijinentertainment.github.io/DagorEngine/api-references/dagor-render/index/daFrameGraph.html>
- East District / Dagor demonstration: <https://www.gaijinent.com/news/demos-of-a-new-gaijins-game-showcase-dagor-engine-power>
- D3D12 ExecuteIndirect: <https://learn.microsoft.com/en-us/windows/win32/direct3d12/indirect-drawing>
- D3D12 enhanced barriers: <https://learn.microsoft.com/en-us/windows-hardware/drivers/display/enhanced-barriers>

## Tool operation after gate removal

`warp`, `probe`, `diagnose`, and `graph-cpu` collect measurements. The `certify` command and performance acceptance budgets have been removed. `evaluate` reads collected samples and reports diagnostic comparisons and real GPU-output mismatches. Runtime failures, unsupported platforms, missing required dependencies, and inconsistent GPU outputs remain errors.

Controllers accept `--processes` (default 1), and `warp`/`diagnose` accept `--variants`. A native executable is required only when the native-cpp variant is selected. Variant order follows the selected list; no fixed interleaving is required. Process scheduling settings are observed, not changed. `graph-cpu` accepts any resource count of at least 4.

Tool self-tests are selected explicitly through `tests/SomeEngine.Graphics.Benchmarks.Tests/SomeEngine.Graphics.Benchmarks.Tests.csproj`; they are outside the default functional solution.
