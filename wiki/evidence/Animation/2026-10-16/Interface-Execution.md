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
