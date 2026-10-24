# Typed animation HUD

The consumer presents 64 cards at 1280 × 720 through the public view, animation,
layout, FLIP, extraction, render preparation and canvas APIs. Each card animates
its fill, corner radius and a `State<float>.Value` reference field. Card positions
reverse every sixteen frames through FLIP. All frames use actual elapsed time.

Build and execution use the common benchmark queue:

```powershell
./tools/Invoke-Benchmark.ps1 dotnet build tools/SomeEngine.Animation.HudChecks/SomeEngine.Animation.HudChecks.csproj -c Release -v quiet '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false'
# After the build result reports exit code 0:
./tools/Invoke-Benchmark.ps1 tools/SomeEngine.Animation.HudChecks/bin/Release/net10.0/SomeEngine.Animation.HudChecks.exe --capture-rgba artifacts/hud.rgba
```

The JSON output records the adapter, publishing configuration, 128 warmup frames,
512 samples, animation CPU, full CPU frame, GPU timestamps, Present call intervals,
allocations, first-frame time and retained memory. The animation P95 budget is
1 ms, fixed in `wiki/evidence/Animation/2026-10-16/Hud-Workload.md` before measurement.
The full frame target is 300 Hz, approximately 3.33 ms.

`Phases` separates Views target submission, Animation, Layout, FLIP, extraction,
render preparation, graph reconciliation, swapchain acquisition, frame execution
with its GPU wait, and presentation. Each phase records median/P95/P99 and current
thread managed bytes per frame.

GPU timestamps enclose the canvas clear and drawing. Each frame waits for its
submission before presentation. Present intervals describe calls to the
Immediate-mode swapchain. The capture runs in one additional frame after the
timed samples and writes tightly packed RGBA8 pixels, 1280 × 720.

For Windows diagnostics with dotnet-trace 9, run its sampled-thread and GC profile
as a separate queued task. The quoted profile and child separator pass intact
through PowerShell to the executable:

```powershell
$animationHud = (Resolve-Path tools/SomeEngine.Animation.HudChecks/bin/Release/net10.0/SomeEngine.Animation.HudChecks.exe).Path
./tools/Invoke-Benchmark.ps1 dotnet-trace collect --profile 'dotnet-sampled-thread-time,gc-verbose' --format Speedscope --output artifacts/hud-profile.nettrace --show-child-io '--' $animationHud
```
