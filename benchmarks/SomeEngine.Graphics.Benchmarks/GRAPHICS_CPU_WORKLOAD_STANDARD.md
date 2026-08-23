# Dagor/Enlisted graphics CPU workload standard

Status: production-distribution-derived RenderGraph + D3D12 CPU standard. This is
one deterministic projection family, not a claim to reproduce a captured Enlisted
frame command-for-command.

## Scope

The metric is the steady-frame CPU interval from immediately before
`RenderGraph.BeginFrame` through real D3D12 command-list close and the final
`Queue.Submit` return. GPU completion waits, `Present`, scene simulation,
visibility generation, asset streaming, GPU execution, and pixel cost are outside
the interval. The CPU-subsystem gate uses one frame slot; its completion wait and
slot recycle occur before timing starts.

Every command-bearing case records real D3D12 `Draw`, `ExecuteIndirect`,
`Dispatch`, and `CopyBuffer` commands. Render targets are 1920x1080, while the
scissor is 1x1 because this is deliberately a CPU benchmark. A counting backend,
64x64-only resources, or callbacks that do no native command recording are not
performance evidence.

The fixed target is P95 below 500 microseconds. This is a target for the bounded
RenderGraph-through-D3D12-submit CPU path, not the complete game render thread.

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
cumulative histogram, so the SomeEngine gate retains the entire official sweep
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
- one of the eight official resource counts (25 through 200);
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

The primary gate is the fixed 200-resource high-watermark case. The smaller
official sweep values are diagnostics only: they can localize scaling but cannot
substitute for or be averaged with the 200-resource result.

The command accepts official sweep values through `--resource-counts`. Omitting
the option runs the fixed 200-resource gate. Explicit smaller values are
diagnostics.

The pass count is held at the production high watermark deliberately. The other
source timeline sizes remain provenance evidence and may be diagnostic cases, but
passing a smaller graph cannot compensate for failing the 73-pass gate.

## Optimization acceptance

An optimization may remain in product code only when all of these conditions hold:

1. The fixed 200-resource production-distribution projection remains correct and
   meets the gate; smaller official sweep values remain available for diagnosis.
2. It has no benchmark name, seed, fixed resource-size threshold, or pass-count
   recognition in product code.
3. Queue consolidation is not reported as an optimization of a multi-queue result.
4. Work partitioning derives from general recording cost and available workers; a
   hard-coded partition count requires independent evidence.
5. Managed allocation/GC is only a secondary diagnostic. Wall-clock attribution,
   native command recording, command-list close, and submit are primary.
6. D3D12 validation/WARP tests pass. Hardware timing runs disable validation, DRED,
   capture tools, synchronized queue validation, and elevated priority.
7. Benchmark builders, seeds, report DTOs, and measurement helpers live only under
   `benchmarks/` or `tests/`; none may remain in a product assembly.

## Measurement protocol

- Explicit hardware adapter; ordinary process and thread priority.
- Use the machine's current background load at ordinary process and thread
  priority. Do not reject a run for high system load, raise priority, set affinity,
  or stop unrelated workloads to manufacture a pass.
- At least 1,024 warm-up frames and 1,024 measured frames. Plateau status is
  reported diagnostically but does not replace the P95 gate.
- One 1,024-sample invocation is the P95 gate. Additional fresh-process runs expose
  current-machine variability but are not averaged into or substituted for that
  invocation.
- Managed allocation and GC counts are not acceptance criteria. Any pause that
  lands inside the wall-clock timing boundary remains part of the measured sample.
- Report P50, P95, P99, maximum, command counts, pass/resource/access counts,
  logical barrier count and barrier-boundary count, queue count, submission count,
  and source revision.
- A final pass requires the fixed 200-resource case to satisfy P95 < 500 us.

## References

- Dagor Engine repository: <https://github.com/GaijinEntertainment/DagorEngine>
- Dagor Engine overview: <https://gaijinentertainment.github.io/DagorEngine/dagor-home/dagor_engine.html>
- daFrameGraph documentation: <https://gaijinentertainment.github.io/DagorEngine/api-references/dagor-render/index/daFrameGraph.html>
- East District / Dagor demonstration: <https://www.gaijinent.com/news/demos-of-a-new-gaijins-game-showcase-dagor-engine-power>
- D3D12 ExecuteIndirect: <https://learn.microsoft.com/en-us/windows/win32/direct3d12/indirect-drawing>
- D3D12 enhanced barriers: <https://learn.microsoft.com/en-us/windows-hardware/drivers/display/enhanced-barriers>
