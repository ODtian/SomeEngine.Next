# SomeEngine trace

The engine records instrumentation through `SomeEngine.Diagnostics`. This tool decodes those recordings and performs headless analysis. It does not install performance thresholds or analysis rules in the engine. A graphical timeline viewer is outside this delivery.

Instrumentation uses C# thread buffers and a C# file writer. It does not use Tracy or EventSource/EventPipe as its transport. Statistical sampling remains on the existing `.nettrace` tooling; sampled CPU time and instrumented thread elapsed time are different measurements.

## Capture

DefaultGame now supplies the in-game scene and measurement platform. Build its content
and the imported scenes first, then run the application without rendering command-line options.
Use F1 to open the panel, F2/F3/F4 for spheres/Sponza/Amazon Bistro, F9 for a measurement
without trace, and F10 for CPU traces of every scene. Detailed and allocation tracing
are selected in that same panel. Traced runs must be compared separately from ordinary timing runs.

```powershell
dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release
./tools/Measure-Scenes.ps1
./tools/Measure-Scenes.ps1 -Trace
```

The script controls the same in-game action and limits the complete application run to 60 seconds.
The Release build produces NativeAOT. Reports include NativeAot and Startup; startup completes when the scene's coarse geometry is ready and a scene frame is presented. F9/F10 requests made during startup run after that first scene frame. With -Trace, the script also captures startup before the window is created; its trace is separate from the later scene traces.
Content building is separate. A scene that does not finish streaming in time is marked incomplete;
it does not receive a passing steady-state timing. Reports and optional `.setrace` files are written
under the application's `Performance` directory. See [current measurements](../../docs/Scene-Performance-2026-09-22.md).

The panel also controls scene playback, TAA, shadows, hardware raster, instance groups,
mesh shaders, asynchronous compute, synchronous CPU/GPU frames, validation and geometry memory.
Keep those conditions, resolution, animation and JIT configuration equal in performance comparisons.
F8 captures the current image separately from performance sampling.

`SomeEngine.Runtime` remains a library. Its startup parser is available to other host applications;
DefaultGame itself no longer uses startup flags for these tests.

## Read and analyze

The panel's Camera sweep playback keeps the moving instances, lights, and material changes and adds camera orbit and rotation around its initial viewing direction. The orbit uses up to 0.45 radians of yaw and 0.1 radians of pitch. Use it separately from the Material changes baseline, whose camera only translates slightly, to exercise changes in visibility without declaring camera cuts.

```powershell
dotnet run --project tools/SomeEngine.Trace -c Release -- inspect capture.setrace
dotnet run --project tools/SomeEngine.Trace -c Release -- analyze capture.setrace --warmup 256 --json capture.json
dotnet run --project tools/SomeEngine.Trace -c Release -- inspect capture.setrace --span 123456789
dotnet run --project tools/SomeEngine.Trace -c Release -- benchmark trace-calibration
```

The analyzer pairs intervals, checks event loss, duplicate/missing records and nesting, then subtracts immediate execution children to obtain exclusive time and allocations. Causal links between threads are retained separately and are never subtracted as nested CPU time. Calls shorter than a clock tick have an empty interval.

Frame elapsed time, main-thread elapsed work, and summed worker-thread elapsed work remain separate. Worker elapsed time is not added to frame latency and is not called CPU utilization. Work retains its originating frame even if it executes before or after that frame's interval; `OutsideFrameMilliseconds` exposes this instead of relocating the cost. Warmup filtering happens after integrity checks and cannot hide damaged records. `Complete` describes data integrity, not performance acceptance.

The calibration command records paired uninstrumented, disabled, enabled, and allocation-enabled loops. It reports both elapsed overhead and the interval measured inside each scope, including the recording tax. It also verifies complete recordings and reports producer allocations. Do not subtract calibration constants from every engine call and present the result as exact native CPU time: clock granularity, inlining, scheduling, and driver behavior affect those intervals.

## Instrument a subsystem

Declare sites once, outside the hot path:

```csharp
private static readonly TraceSite UpdateTrace = new("ecs", "Update");

using (EngineTrace.Begin(UpdateTrace))
{
    UpdateEntities();
}
```

Call `EngineTrace.Start(path, options)` once around the capture lifetime and dispose it after the desired work is finished. Frame IDs passed to `BeginFrame` are positive and unique within a recording. Keep CPU scopes on the thread that opened them; they must not cross `await`.

Capture `EngineTrace.Context` when scheduling asynchronous work and use `EngineTrace.Associate(context)` around that work's synchronous execution. The built-in Job scheduler and renderer game-update task already propagate this context. Actual execution nesting is independent of causal ancestry, including when a waiting thread helps execute a job originating in another frame.

Sites store labels once. Events contain numeric IDs and fixed-size data. The producer owns its current block; preallocated bounded queues transfer complete blocks to a dedicated writer thread. A producer never waits for disk IO. Pool exhaustion, nesting-depth exhaustion, or ID exhaustion increment explicit loss counts; misuse is reported separately. Such a trace is unsuitable for complete cost accounting.

The current format is little-endian `SETRACE1`, version 2, with a clock header, blocks of 56-byte records, and a completion footer containing sites, threads, event totals, and loss counts. The decoder rejects unsupported versions and truncated files. Windows thread metadata includes both managed and OS thread IDs for correlation with `.nettrace`; other platforms currently report OS thread ID as unavailable (`0`). Raw files are retained independently of derived reports.
