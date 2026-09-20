# SomeEngine trace

The engine records instrumentation through `SomeEngine.Diagnostics`. This tool decodes those recordings and performs headless analysis. It does not install performance thresholds or analysis rules in the engine. A graphical timeline viewer is outside this delivery.

Instrumentation uses C# thread buffers and a C# file writer. It does not use Tracy or EventSource/EventPipe as its transport. Statistical sampling remains on the existing `.nettrace` tooling; sampled CPU time and instrumented thread elapsed time are different measurements.

## Capture

Build the DefaultProject content first with `dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj -c Release -- --engine <repository-root>`. The executable is Game.App; SomeEngine.Runtime is a library. See [the integrated baseline](../../docs/Integrated-Frame-Baseline-2026-09-20.md) for measured settings and limitations.

```powershell
dotnet run --project templates/DefaultProject/Game.App/Game.App.csproj -c Release -- --frames 512 --hidden-window --no-vsync --trace capture.setrace --trace-detail --trace-allocations
```

The destination must not already exist. `--trace-detail` includes RHI/native call sites and individual RG row updates. Without it, the recording retains application, ECS, Job, and RG intervals. `--trace-allocations` additionally reads each thread's managed allocation counter at scope boundaries.

`--trace-frames-only` records frame intervals and counters while suppressing operation scopes and context associations. This is the low-overhead control for quantifying the effect of detailed instrumentation; it cannot measure worker costs or allocation breakdowns.

For repeatable renderer measurements with all work completed in its frame:

```powershell
dotnet run --project templates/DefaultProject/Game.App/Game.App.csproj -c Release -- --frames 512 --hidden-window --no-vsync --synchronous-frames --scene-playback materials --fixed-step 0.016666667 --trace capture.setrace --trace-detail --trace-allocations
```

`--synchronous-frames` updates and extracts the current scene in the current frame, and waits for its GPU completion events before presenting. It retains the actual update, extraction, recording, submission, and wait costs. It does not pipeline scene work into the next frame. Normal application scheduling remains available when this diagnostic mode is not selected.

The application's `motion` playback moves authored mesh instances, the camera, and point lights. `materials` additionally changes standard-material roughness every frame. These are controlled scene inputs, not a claim to reproduce an entire AAA game. The trace includes the numbers of moving meshes, moving lights, and changing materials. `--no-async-compute` provides a separate queue-strategy comparison. Image/GPU validation runs should be reported separately from timing runs because readback and validation themselves cost time.

## Read and analyze

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
