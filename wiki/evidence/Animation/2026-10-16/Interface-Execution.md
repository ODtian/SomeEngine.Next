# Interface animation execution

The application uses the public animation phases before layout and FLIP.
`GameFrameSystems.AddAnimation(spec)` registers concrete motion combinations,
`Describe()` expands the actual execution order, and `MeasureViews()` records
animation CPU time in `ViewTiming.AnimationMs`.

The Release `GameFrameTests` selection passed **4 / 4** on 2026-10-16. It covers
the public frame description, user systems with animated layout, and FLIP
continuity through both ordinary World and WorldJobs execution. The pinned
Alimer.Bindings submodule was initialized for the Runtime dependency build.

| FIFO task | Command | Result |
| --- | --- | --- |
| `2b0434dd1287441997639beb6d136029` | `dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~GameFrameTests -v quiet -m:1 -nodeReuse:false --property:UseSharedCompilation=false` | exit 0; 4 passed |
| `0e6f715cd68c4845a9e926e424d89c10` | Release HUD consumer build, serial compilation and shared compilation disabled | exit 0; 0 warnings, 0 errors |

Raw commands, working directories, timings and output:
[Runtime result](runtime-frames.result.json), [stdout](runtime-frames.stdout.txt), [stderr](runtime-frames.stderr.txt);
[HUD build result](hud-build.result.json), [stdout](hud-build.stdout.txt), [stderr](hud-build.stderr.txt).

The HUD workload and its P95 animation budget were fixed before execution in
[Hud-Workload.md](Hud-Workload.md). Its presentation path is implemented in
`tools/SomeEngine.Animation.HudChecks`.

The public typed view selection passed **10 / 10** in the checked, zero-worker
WorldJobs consumer (`aba634bc1adf430c806623a2aa48184e`). It covers generated ordinary
targets, unchanged progress and held versions, reference presentation, reversal,
parameter updates, consumer history, awaited continuation, cancellation and both
root and leaf dependencies. Suspended target commits use `Untrack`; the resumed
reference target continues from 2 to 16 and presents 9 after 0.5 seconds.

The first HUD capture and measurement are retained as the optimization baseline:
[result](hud-initial.result.json), [stdout](hud-initial.stdout.txt),
[stderr](hud-initial.stderr.txt), [swapchain capture](hud-initial.png).
Its animation median/P95/P99 are 1.6558/1.8595/1.9911 ms, with 0 B per animation
frame. The fixed animation budget is 1.00 ms P95. Full CPU median/P95 are
7.3052/9.3714 ms; GPU median/P95 are 0.052224/0.124928 ms; full managed allocation
is 142286.359375 B per frame.

The following optimization keeps the 64 cards, inputs, budget, 128 warmup frames
and 512 samples. Empty algorithm buffers leave their query row early; inline
outputs update source caches through the current row. View updates compare
positions directly, select their component's animated fields, reuse typed
bindings and skip child cleanup for calculations that declare their own values.
The HUD records time and allocation for Views, Animation, Layout, FLIP, extraction,
preparation, graph reconciliation, acquisition, execution and presentation.

The optimized consumers built with **0 warnings / 0 errors**, and their public
ECS, typed algorithm, view, leaf and condition checks passed. The affected
property selection passed **5 / 5** and calculation/async retention selection
passed **4 / 4** (`4f07f36747cd4d9bbb71375f9f417f29`):
[commands](hud-optimization-checks.ps1), [result](hud-optimization-checks.result.json),
[stdout](hud-optimization-checks.stdout.txt), [stderr](hud-optimization-checks.stderr.txt).
The dedicated view selection is retained as [result](view-fields.result.json),
[stdout](view-fields.stdout.txt), [stderr](view-fields.stderr.txt).
