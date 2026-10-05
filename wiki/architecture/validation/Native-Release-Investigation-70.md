# Public local-light native release investigation — #70 / #125 / #135

## Verdict

**Not reproduced in this concentrated window; root cause remains unknown and no native release fix is claimed.** The original three `0xC0000005` testhost aborts remain failed process runs. Passing rendering or a later completed batch does not turn those failures into passes or skips. Native reliability and actual default three-scene acceptance remain open.

The delivered change is a public repeated-startup regression plus this investigation/evidence, **not** a production repair. No COM release catch, finalizer suppression, forced GC, native mutation, retry inside a test, feature reduction, or engine-wide lifetime change was made. No denied source/compiler/issue/private Epic operation was retried. No default three-scene or performance run occurred. GPU was released after the final local batch.

## Source and isolated checkout

- Tree: `F:/SomeEngine.AgentWork/rg70-remaining/trees/native-release`.
- Branch: `agent/rg70-native-release`.
- Confirmed clean base: `86555c9fbf9651d0eaf9d4916b2b571d10a58f9d`, the actual #135 delivery, not the older a0 integration.
- Regression-only commit: `82f7814e`.
- Integration `7e31104ea85e1fcbbeb44813f26976a314fcc3a8` merged without conflicts as **`0755a124862bab28d5f77ec667a696b03aacccd5`** before the final executable build/batch. That integration tree equals the #135 delivery; synchronization introduces no new production delta.
- All final comparisons/captures identify `0755a124…`. Subsequent delivery changes only documentation/evidence.

The protected main/integration/historical/other-agent/delivered checkouts were not modified. No push, PR, or issue closure occurred.

## Original failure, distinguished from assertions

The copied, already permitted records remain at their original committed locations:

| Failed process record | Run outcome | Total / executed / result rows |
| --- | --- | --- |
| `artifacts/acceptance/reference-125/gpu-window/local-light-native-aborted.trx` | Failed | 0 / 0 / 0 |
| `artifacts/acceptance/reference-125/gpu-window/point-range-native-aborted.trx` | Failed | 0 / 0 / 0 |
| `artifacts/acceptance/ticket-135/gpu-window/local-spot-recovery.trx` | Failed | 0 / 0 / 0 |

Each retained log reports `0xC0000005`, `Marshal.Release → ComObject.Finalize → GC.RunFinalizers`. `inherited-aborts.json` records hashes and process classification separately from the new completed-case totals. There is no new crash dump: the crash-capture runs below completed normally.

The #135 TRX stdout reports the first application completed **63 frames**, then the native fatal error, without any completed test result. The recovery test source performs three public application captures before its first RT comparison. That ordering and stdout are useful localization evidence, **not** identification of the released COM instance or the exact instruction. In particular, the renderer's completion line does not prove test/application cleanup success, and the record does not establish an RT-reference-buffer bug. #135's earlier receiver-expansion baseline also passed; it remains non-red compatibility evidence.

## Ownership/lifetime source investigation

Read AGENTS, GLOSSARY, domain/Behavior-Checks, RHI lifetime/queue guidance, and paired the permitted current implementations with their callers. `inspected-source-hashes.csv` fixes the source bytes examined.

| Boundary and primary holder | Current source behavior / observation |
| --- | --- |
| `LocalLightRayShadowTests.LocalScene` | Holds fixture content/output paths. `CaptureAsync` awaits public `Application.RunAsync`, records its typed result and content hashes, then loads a managed `SceneLinearImage`. Fixture disposal deletes only its own content; it does not directly release native GPU objects. |
| `RuntimeApplication.RunAsync` / `RuntimeShaderResources` | Each application run creates a shader-resource collection and an application display. The shader collection holds global sessions and resident shaders; the display owns backend/device/surface/swapchain shutdown. No test-only lifetime holder or static retention was added. |
| `RuntimeApplication.RunRenderer` | Readback/capture occurs through submitted-frame output, including actual jitter/transforms. Shutdown joins the outstanding update and waits tracked submissions before releasing output verifiers/render features. The outer application display then disposes swapchain/device/surface/backend/window. |
| `RayShadowImage.TraceAsync` | Holds its backend/device and a single reverse-disposed list of caller-created RT buffers/structures/views/pipeline. Both BLAS/TLAS construction and trace/copy submissions are explicitly waited to `WaitStatus.Completed`; mapping is disposed before the resource-list cleanup. The method returns a managed `uint[]`. No proven early buffer release was found in this path. |
| D3D12 `Completion`, `CommandLifecycle`, `Device`, `D3D12Backend` | `WaitCpu` checks the completion fence, not elapsed time. Accepted command payloads are retired at completion; device teardown quiesces payloads before child/native teardown. Backend teardown destroys registered roots before freeing the SDK/loader; incomplete teardown retains native state. This source inspection is not a proof that every native/driver path is fault-free. |
| `Shader.Slang`, `ShaderSourceBlock.Slang`, D3D12 `Pipelines` | A resident `Shader` retains global/session/modules/entries/linked code/blobs. The native pipeline holds its linked Slang component reference until pipeline release. No direct `Marshal.Release` is performed by the RT fixture. |

The installed **public SlangShaderSharp 2026.17.1** package uses source-generated COM wrappers (package repository commit `3972d6acb071699f17277c4fbe2060315c1a5a37`). Targeted decompilation of the installed public managed interfaces confirms that module-returning session methods use `NoFreeComInterfaceMarshaller`, whose `Free` is empty and whose conversion delegates to `ComInterfaceMarshaller`. Relevant small API excerpts and package digests are retained. This makes Slang wrapper lifetime a reasonable investigation area; it **does not** prove a double release, wrong native refcount, or cause of these crashes. No native Slang/private source was obtained, and no package/binding fork was made.

**No evidence-backed choice of a faulty primary holder was possible.** Artificially retaining shaders/sessions until process exit, changing engine teardown globally, or applying `KeepAlive` on speculation would hide the uncertainty rather than establish the requested fix. None was done.

## Public regression and TDD qualifier

`LocalLightRayShadowTests.RepeatedLocalLightStartupAndShutdownPreservesIndependentShadows` expresses the confirmed public seam: startup → default render/raw output → independent source-triangle hardware RT comparison → clean shutdown → another startup.

It performs **three** ordinary spot-light fixture cycles in the same testhost, each with the existing shadowed/unshadowed/ambient controls and full-receiver accuracy assertions. The finite loops are the repetition being specified, not crash retries. Default TAA/async/available mesh/mixed raster functionality remains enabled. The existing no-VSM controls remain controls, not a reduced feature route for the shadowed output. No private state, friend, probe, message assertion, static mutable state, sleeps, forced GC, or threshold change was added.

The test was written and run before any attempted repair. **It passed on unchanged production.** Consequently there is no newly observed red and no red→green native fix claim. The historical aborts are real failing process observations, not a manufactured red for this new test. Runs stopped after the final coherent batch rather than continuing isolated cases until a desired result appeared.

## Actual execution, not authored/discovered estimates

Hardware: D3D12, **NVIDIA RTX 3080**, driver **32.0.16.1074**. SDK **10.0.112**, runtime **10.0.12**; environment/runtimeconfig/executable digests retained.

| New record | Result rows | Executed / passed | Failed assertions | Skips | Scope |
| --- | ---: | ---: | ---: | ---: | --- |
| baseline-batch | 6 | 6 / 6 | 0 | 0 | Ordinary unchanged six-case class |
| baseline-diagnostic | 6 | 6 / 6 | 0 | 0 | Same source; crash collection enabled |
| repeated-startup-before | 1 | 1 / 1 | 0 | 0 | New regression, unchanged production; crash collection enabled |
| regression-batch-before | 7 | 7 / 7 | 0 | 0 | Full class plus regression; crash collection enabled |
| **final-batch, source 0755a124** | **7** | **7 / 7** | **0** | **0** | **Ordinary same-batch run after integration merge** |

Five fresh completed GPU testhosts: **27 executed / 27 passed / 0 failed / 0 skipped**. This is repeated-observation accounting, not 27 distinct behaviors or a native fix verdict. `capture-counts.json` records **99 completed public application captures and 41 comparisons** across those runs. The final seven-case batch contains **27 application captures / 11 comparisons**, including both old/new recovery footprints and all three repeated-startup comparisons. The same-batch TRX and raw comparisons are the final result, not a sum of isolated per-case successes. Final outer wall **167.4335518 s** is correctness-run wall time, not GPU time or performance evidence.

Final ordinary command (with owned `SOMEENGINE_SHADOW_EVIDENCE`, `SOMEENGINE_SHADOW_SOURCE` and `SOMEENGINE_TEST_ARTIFACTS`, restored afterward):

```powershell
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~LocalLightRayShadowTests --results-directory <owned-proof>/final-batch --logger 'trx;LogFileName=final-batch.trx'
```

Diagnostic runs add only `--blame-crash --blame-crash-dump-type mini`; no native mutation or renderer option changes. All run arguments, source boundaries, test-process exits and walls are in individual command JSONs/logs/TRX. The first baseline shell wrapper ended with exit1 from an optional `Get-Command` lookup for absent debugger tools after the test subprocess had exited0; its six Completed TRX rows are the actual test result, not a shell-exit inference. Standard x64 CDB/WinDbg paths were absent; no debugger was installed or downloaded. No new discovery/list-tests operation is claimed.

### Final independent results and unchanged gates

All eleven final comparisons have **100% rendered-receiver coverage / zero unexpected self-hits**. Final range/cone rejects remain **1915 / 20075**. Both recovery new footprints capture scene/submitted **63/62**, with old footprint **31/31** retained.

| Final case | Shadowed / lit | Mismatch AND mean | False / lit | Missed / shadowed |
| --- | ---: | ---: | ---: | ---: |
| point | 3839 / 38812 | .1078521% | .0566835% | .6251628% |
| spot; repeated-startup cycles match | 3839 / 38812 | .11957516% | .07471916% | .57306592% |
| point-range | 2894 / 37846 | .26018654% | .1664641% | 1.4858328% |
| spot-cone | 3839 / 18938 | .2151293% | .13200971% | .6251628% |
| point recovery, new footprint | 4449 / 38149 | .12911404% | .086502923% | .49449313% |
| spot recovery, new footprint | 4449 / 38149 | .15493685% | .10223073% | .60687796% |

Pixel error >.15; mismatch **and** mean/false/missed ≤2%; coverage ≥98%; ≥32 shadowed/≥512 lit; zero self-hit. All eligible receivers and edges remain included; indeterminate color ratios remain errors. Point fixtures cover three cube faces, not all six. Configured 64-page recovery pressure is not proof of a particular missing/fallback/no-mip state. A completed spot recovery now exists on the new source, but does not retroactively repair the aborted #135 run.

## Preparation, CPU checks and retained evidence

Fresh own compatible DefaultProject content was cooked using Game.Build: **68 assets plus catalog = 69 payload records**, catalog SHA256 `4803D02C5E4372D0417D09E00E5510387E3DF1B41BFB52178252964C137E8A80`. Original sphere source hash remains `40523D69E4E2C6FC1316114008B3DE26100108A3A5ED2A5601FF1B9FF6C89A83`. Final recheck: **all69 hash/size matches**. Cook-generated import metadata was restored only in this owned checkout; cooked binary content was not removed or replaced with historical content.

Three owned Cluster Release builds (baseline/regression/final) each completed with **0 warnings / 0 errors**. Post-GPU device-free `FrameOutputTests`: **5 passed / 0 failed / 0 skipped**; the denied standalone reference shader test was not selected. Completed default rule checker: **24 projects / 0 violations / exit0**. Its first outer command timed out at180 seconds before checker completion and is retained separately as incomplete, not a pass; the later full checker has its own log/command/exit. Post-CPU final test-directory executable digests show **0 changes** from the executed final manifest. That does not assert identity with any earlier agent's binaries.

Evidence root: `artifacts/acceptance/native-release/`. It retains final raw/beauty/sidecars, capture/application provenance, full receiver CSV/PNG/BMP/comparisons, every new TRX/log/command, initial base/clean check, content/executable/package/source/environment hashes and the failed historical-run index. Redundant successful exploratory capture directories and used decompilation scratch were removed only from this agent's own output; their logs/TRX/commands/counts remain. Relevant small public binding excerpts and the complete final batch remain. No crash evidence or other-agent files were cleaned.

**Outstanding:** reproducible native fault plus identity/ref-lifetime diagnosis and verified repair; meaningful baseline-sensitive production/numerical red; exact missing/unfinished/coarse/recovery semantics; all-six faces/alpha/deformation/backend/partial-submit coverage; actual coherent default three-scene RT/beauty and matched performance; final combined Standards/Spec. #70/#125/#135 remain open. Parent owns integration/full final checks and any next authorized investigation.
