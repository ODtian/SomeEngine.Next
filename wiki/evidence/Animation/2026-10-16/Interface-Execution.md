# Interface animation execution

The final normal HUD run passes the fixed **1.00 ms animation P95 budget**:
**0.9249 ms P95**, **0.7823 ms median**, **0.9938 ms P99**, and
**0 B per animation frame**. It uses the original **64 cards, 128 warmup
frames and 512 samples**, with D3D12 at 1280×720 on the RTX 3080.
[Final capture](hud-final.png), [sampling summary](hud-final.sampling.json),
[command](hud-final.commands.ps1), [result](hud-final.result.json),
[stdout](hud-final.stdout.txt) and [stderr](hud-final.stderr.txt) retain
FIFO run `99887e6cccc3487cad94845835140a19`, measured from source `11da31ce`.

| Timing boundary | Median ms | P95 ms | P99 ms |
| --- | ---: | ---: | ---: |
| Animation CPU | 0.7823 | 0.9249 | 0.9938 |
| Complete CPU frame | 6.1221 | 7.8269 | 8.9015 |
| GPU timestamp interval | 0.224256 | 0.365568 | 0.37888 |
| Actual Present interval | 6.1275 | 7.8329 | 8.9079 |

Views median/P95/P99 are **2.6568/2.8887/3.1600 ms**, with **0 B/frame**.
Complete managed CPU allocation is **134084.71875 B/frame**, including
extraction, preparation and rendering execution. The recorded full-frame target
is **3.333333 ms**. The first frame costs **6063.9591 ms**, and retained managed
memory is **15654304 B**. The Release HUD build completed with **0 warnings /
0 errors**; the same FIFO task passed
`CompletedSourceRespondsToLayerWeightChanges` **1 / 1**.

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

The first optimized normal run (`35b3387e05404e619dbc5f2ec4ec3ba8`) measured
animation median/P95/P99 **1.1548/1.3875/1.6702 ms**, with **0 B/frame**.
Views median/P95 were **3.4471/4.3718 ms**. The full frame median/P95 were
7.4355/9.3842 ms and the GPU median/P95 were 0.091136/0.116736 ms.
[Result](hud-first-optimization.result.json),
[stdout](hud-first-optimization.stdout.txt), [stderr](hud-first-optimization.stderr.txt)
retain all ten phase costs and allocations.

The subsequent sampled-thread/GC trace (`e06f813e333840b88cda6aff236c4e66`)
ran the same consumer. Its sampled stacks identified repeated field declaration
buffer borrows, source lookup during target submission and per-row timeline
enable reads. This diagnostic run is recorded separately from normal performance
samples: [result](hud-profile.result.json), [stdout](hud-profile.stdout.txt),
[stderr](hud-profile.stderr.txt). The next implementation shares typed field
declarations through AnimatedField, reuses an already read component root and
selects active algorithm/phase rows before borrowing their state.

The normal run after phase selection and shared view declarations
(`532917a522354c61b39a5b92956a832a`) measured animation median/P95/P99
**0.9196/1.0997/1.1928 ms**, with **0 B/frame**. Views median/P95 were
**2.7042/2.9915 ms**, also at **0 B/frame**. Full CPU median/P95 were
**6.3903/8.4177 ms** and GPU median/P95 were **0.224256/0.354304 ms**.
The fixed workload remains 64 cards, 128 warmup frames and 512 samples;
the animation P95 is compared with the original **1.00 ms** budget.
[Command](hud-532917.commands.ps1), [result](hud-532917.result.json),
[stdout](hud-532917.stdout.txt), [stderr](hud-532917.stderr.txt),
[sampling summary](hud-532917.sampling.json) and
[swapchain capture](hud-532917.png) preserve this iteration separately.
This command built the public Checks and HUD consumers with **0 warnings /
0 errors**, passed the default public consumers, and passed the affected
property/weight/manual-track selection **3 / 3**.

The declaration and presented-value selection passed **15 / 15**:
**11 TypedAnimationTests** and **4 ReadersFollowPresented cases**.
It covers shared declarations, changing parameters, async reference targets,
and dependencies that observe the presented ordinary and reference fields.
[Result](view-declarations-tests.result.json),
[stdout](view-declarations-tests.stdout.txt) and
[stderr](view-declarations-tests.stderr.txt) retain the command and output.

Splitting enabled and disabled timeline queries moves the enabled flag from
per-row World access to the query's batch state. Run
`566b9f99524645f08c258aa547c839d6` measured animation median/P95/P99
**0.8498/1.0106/1.1517 ms**, with **0 B/frame**, using the same fixed workload.
Views median/P95 were **2.6346/2.9296 ms** and **0 B/frame**; full CPU
median/P95 were **6.2316/8.2716 ms**. The Release build had **0 warnings /
0 errors**. [Command](hud-566b9f.commands.ps1),
[result](hud-566b9f.result.json), [stdout](hud-566b9f.stdout.txt),
[stderr](hud-566b9f.stderr.txt), [sampling summary](hud-566b9f.sampling.json)
and [swapchain capture](hud-566b9f.png) preserve the iteration.
The affected `CompletedSourceRespondsToLayerWeightChanges` public check passed
**1 / 1** (`fba27963557c47e8b923f83f454cb0b7`):
[result](hud-414-held.result.json), [stdout](hud-414-held.stdout.txt),
[stderr](hud-414-held.stderr.txt).

The final iteration uses `ReadSpan` for track selection, acquires one writable
track span when a row first enters the sampling path, and reuses that span for
input and output traversal. Finished and disabled tracks retain their existing
read-only path. This removes repeated buffer indexing, track copies and version
recording inside a batch. The final run and its affected public check are linked
at the start of this page; all earlier captures and measurements remain above.
