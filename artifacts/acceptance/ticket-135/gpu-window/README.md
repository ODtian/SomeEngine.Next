# #135 serialized validation — partial / native blocker / GPU released

## Source and ownership

- Resumed clean `602bda9249d5439fb354ff7f1c9943d285ef7908` in `agent/rg70-ticket-135`.
- Merged actual #125 partial integration `a0a17365e2055cde8dabd55543d5b6570409b4e5` without conflicts as `ff8aafc8e4a009d2e2f34d51872055d02b5c31f2`.
- Production resource-interface corrections: `d94bcbf8571acacc2c686998af51f562da00db99` (administrative page producers exclude material mask declaration) and `d497c04d` (primary neutral target-policy HiZ metadata binding).
- **Coherent checked source/content checkpoint: `9333101b7920176b90d5f10c886505182a402086`**. Only regenerated import metadata after d497c04d; no production changes after the checks.
- New detached baseline was clean at `d6085c7db41397db09c2e53cf143de5547fe88a3`, using its own pinned submodule/source-compatible Runtime fixture cook. It was removed after the test completed and its result was retained here.
- Reference/capture/local fixture/comparator/native source ownership preserved. No denied root/sampling/resource/native/compiler-test/Issue retry, private Epic or historical denied traversal. No protected source modification, push, PR or issue closure.

The updated source/root-cause page is `wiki/architecture/validation/Vsm-Depth-Coverage-135.md`. The original parent-folder CPU evidence remains earlier provenance, not rewritten to match this generation.

## Actual tests — no baseline red claim

`test-totals.json` is calculated from actual TRX result rows and summaries. Capability skips come from NotExecuted rows, not xUnit's misleading zero summary skip field.

| Record | Rows | Executed | Passed | Failed | Skipped | Run |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| receiver-expansion-baseline | 2 | 1 | 1 | 0 | 1 | Completed — **not red** |
| receiver-expansion-final | 2 | 1 | 0 | 1 | 1 | Failed before any completed frame; 6 vs 5 binding count |
| receiver-expansion-after-resource-fix | 2 | 1 | 0 | 1 | 1 | Failed before any completed frame; 24 vs 23 binding count |
| receiver-expansion-coherent | 2 | 1 | 1 | 0 | 1 | Completed |
| public-transitions | 67 | 34 | 34 | 0 | 33 | Completed |
| headless-work-material | 17 | 17 | 17 | 0 | 0 | Completed |
| local-point | 1 | 1 | 1 | 0 | 0 | Completed |
| local-spot | 1 | 1 | 1 | 0 | 0 | Completed |
| local-point-range | 1 | 1 | 1 | 0 | 0 | Completed |
| local-spot-cone | 1 | 1 | 1 | 0 | 0 | Completed |
| local-point-recovery | 1 | 1 | 1 | 0 | 0 | Completed |
| **local-spot-recovery** | **0** | **0** | **0** | **0 result rows** | **0** | **Failed native-aborted run** |

Coherent completed scopes: **91 rows / 57 executed and passed / 0 assertion failures / 34 Vulkan Presentation skips**, plus the **separate failed zero-result native run**. This total excludes the baseline and both corrected-interface failures. It is not a full acceptance pass. No separate list-tests command ran; selected/discovered rows are actual result rows.

Runtime skips: `NotSupportedException`, Vulkan adapter lacks Presentation. Both headless backends actually passed; presentation support is not inferred from them.

The prepared full-image receiver-expansion test **passed on the unchanged production baseline**, so it is compatibility coverage only. No mutation/receiver removal manufactured an apparent red. The later zero-frame source-interface failures and correction green are real, but not the required wrong-baseline shadow numerical red.

## Independent local truth on this source

Five successful fresh one-row hosts use source `9333101b`. Their exact filters/source/exits/35.9–39.3s walls are in `local-commands.json`. Raw controls, actual frame/jitter/transforms/settings, source content hashes, CSV and full images remain under `local-lights/`. The source rays/comparator/thresholds were not edited.

| Case | Shadowed / lit | Mismatch AND mean | False/lit | Missed/shadowed |
| --- | ---: | ---: | ---: | ---: |
| point | 3839 / 38812 | .1078521% | .0566835% | .6251628% |
| spot | 3839 / 38812 | .11957516% | .07471916% | .57306592% |
| point-range | 2894 / 37846 | .26018654% | .1664641% | 1.4858328% |
| spot-cone | 3839 / 18938 | .2151293% | .13200971% | .6251628% |
| point recovery, new footprint | 4449 / 38149 | .12911404% | .086502923% | .49449313% |

Completed comparisons, including point recovery old footprint: coverage100%, self-hit0. Boundary/interior and every valid geometric receiver retained. Range boundary mismatch3.6244% remains in aggregate. Independent range/cone rejects1915/20075, point three faces (not six). Recovery scene/submitted63/62 aligns canceled-frame jitter; old footprint31 also passes. Configured64-page pressure is **not** diagnosed missing/coarse/no-resident-mip evidence.

## Native abort and stopping boundary

`local-spot-recovery.log/.trx`: **0xC0000005**, Marshal.Release → ComObject.Finalize → GC.RunFinalizers. Failed run, no completed test/result row/spot recovery comparison. GPU work stopped and scheduling window **released**. No repeat, native patch or denied native-source workaround. This is an unknown infrastructure blocker, not a diagnosed production VSM fault.

#125's two inherited zero-result native aborts remain failed runs in its separate committed evidence. Its later six passes are scoped to that source and do not erase this source's abort or prove batch reliability.

Default THREE-SCENE directional RT/beauty, Sponza/Bistro owned import, further GPU cases, exact missing/unfinished/fallback state proof and performance **did not run after this blocker**. `Measure-Scenes` had also been explicitly deferred pending #81 CPU-build idle permission; no preliminary throughput observation or timing claim exists.

## Commands / CPU checks / hashes

All commands used absolute owned proof destinations and restored SOMEENGINE_SHADOW_EVIDENCE/SOURCE/TEST_ARTIFACTS afterward. No shared boolean-mode driver, private test API, queue/storage assertion, sleeps or exception-message assertion was introduced.

```powershell
# Baseline, new own detached source tree; exact baseline commit above:
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~VsmCoverageImageTests --results-directory <this-proof> --logger 'trx;LogFileName=receiver-expansion-baseline.trx'
# Current coherent source, public startup/output:
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~VsmCoverageImageTests --results-directory <this-proof> --logger 'trx;LogFileName=receiver-expansion-coherent.trx'
$filter = 'FullyQualifiedName~ShadowImageTests|FullyQualifiedName~VsmWindowImageTests|FullyQualifiedName~RasterWorkFailureTests|FullyQualifiedName~RenderingFeatureTests|FullyQualifiedName~GeometryRenderingTests|FullyQualifiedName~HardwareFrameLoopDeliversRequestedImages'
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --no-build --filter $filter --results-directory <this-proof> --logger 'trx;LogFileName=public-transitions.trx'
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project <owned-DefaultProject> --engine <owned-engine>
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~VsmWorkRenderingTests|FullyQualifiedName~ReplacingRasterMaterialMatchesFreshInstallation|FullyQualifiedName~ClusterPipelineOutputTests|FullyQualifiedName~AddedTessellatedMaterial|FullyQualifiedName~RemovedTessellatedMaterial' --results-directory <this-proof> --logger 'trx;LogFileName=headless-work-material.trx'
# Each row in local-commands.json ran in a separate host:
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter <recorded-filter> --results-directory <this-proof> --logger 'trx;LogFileName=local-<name>.trx'
# GPU stopped. Final CPU-only checks:
pwsh -NoProfile -File tools/Check-TestRules.ps1 -ResultsDirectory artifacts/acceptance/ticket-135/gpu-window/rules-final
dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release -v minimal
git merge --no-edit codex/render-graph-regions-70
git diff --check
```

Final three Release builds each exit0, 0 warnings/errors; checker24 projects/0 violations; content cook68 assets. No denied ReferenceShaderTests/standalone source compiler ran. SDK10.0.112/runtime10.0.12. `content-payloads.csv`: all68 owned cooked assets plus catalog, hashes/sizes. `source-executable-hashes.csv`: checked source and actual pre-final-CPU executable bytes; subsequent CPU rebuild identity is separately checked, not assumed. `post-cpu-byte-check.json` records **5 executable-byte differences** (Game.App exe/DLL, its Runtime DLL and both test DLLs), with matching sizes and unchanged production source. All69 cooked payload/catalog hashes still match, along with runtimeconfig. Retained executed-byte hashes were **not** rewritten; do not claim the current files are byte-identical to those used by the preceding tests. `dotnet-info.txt`, `jit-environment.csv`, raw sidecars/capture.json and full comparisons retain configuration and adapter provenance. No calibrated GPU timing is inferred from wait time.

No temporary probe/script was created. Successful fixtures disposed their temporary content; targeted catalog discovery found no leftover local fixture content. The used detached baseline tree was removed; only required results are retained. No other worker/historical evidence cleanup.

#135 remains partial/not closable. Keep #125/#127/#81, full Standards/Spec, exact page-state/recovery behavior, baseline sensitivity, native reliability, actual default three-scene beauty/RT and matched complete performance gates open. Independent thresholds and all receivers are unchanged.
