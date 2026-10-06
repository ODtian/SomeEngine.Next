# Selected-frame linear shadow references (#125)

原始输出已移入仓库外验收归档；文中的 `artifacts/acceptance/` 路径对应归档内目录。归档身份与结果摘要见 [#70 验收记录](../../../docs/agents/acceptance-70.md)。

## Status and scope

**Initial checkpoint: CPU-tested partial delivery. Later public capture/six-case GPU validation is recorded in [Shadow-Reference-Gpu-125](Shadow-Reference-Gpu-125.md); full #125 acceptance remains partial.** Branch `agent/rg70-reference-125`, initially clean at integration `35295fbfb429a451fb0f591fde84f76839bb9414`. No production Cluster/VSM shader, `VirtualShadowFeature`, `ClusterPipelineSystem`, or Runtime `ShadowImageTests` modification by this slice. #134 was subsequently merged; #135 retains its production responsibilities. The integration coordinator records the delivery commit separately.

Read project AGENTS/GLOSSARY/domain/issue-tracker/Behavior-Checks and the supplied acceptance/evidence/coordination/merge pointers. Live `gh issue view` requests for #125/#132/#70/#127 were denied by the execution guard; no retry or substitute remote retrieval occurred. Therefore this implementation follows the explicit assigned requirements and retained acceptance guidance, not a new live-issue verification.

## Public capture capability

`RuntimeStartupOptions` now accepts:

- `--linear-frame-output <file.rgba16>`: raw linear material color from the existing rendering pipeline, before TAA/tone mapping;
- `--capture-frame <zero-based scene frame>`: the selected frame, including frame 0;
- existing `--frame-output <file.bmp>` remains presentation/beauty output.

Finite runs select the last scene frame by default. Both destinations can be requested together. Earlier frames do **not** acquire a readback verifier, record a copy, or wait merely because a final output destination exists. Normal frames-in-flight/resource waits remain. `--verify-frame-output` still deliberately verifies/waits each frame; explicit synchronous mode is unchanged. For compatibility, an indefinite BMP-only run without an explicit selection retains its previous latest-frame capture behavior. Indefinite linear output requires an explicit frame. A selected frame that was never submitted does not produce successful requested output.

`SceneEdit.CaptureLinearImage(Action<SceneLinearImage>)` extends the existing public scene-action/image capability. It does not expose private renderer buffers, counters, or a second scene-to-GPU route. Both capture formats reuse `FrameOutputVerifier`'s graph copy/readback; a frame region between shading and temporal resolve reads the ordinary material-color output. Beauty retains the presentation path. When both are selected there is one completion wait, not two. Readback/callback delivery follows completed-frame work-failure checking.

`SceneLinearImage` contains packed top-down Half RGBA and `SceneFrame`. `Save` writes little-endian RGBA16F with no header/padding, plus `<path>.json`; `Load` reads both without a device. Width/height in the sidecar determine the byte count. HDR values are preserved rather than inverse-tone-mapping quantized beauty.

Frame data records the scene frame and submitted-frame indices separately, elapsed scene time, camera position, view, **actual jittered projection**, unjittered projection, jitter pixels, clip planes, mesh asset IDs/transforms, authored light kind/range/cone/color/settings, startup options, temporal settings, raster-edge/work capacity, adapter/driver and configured VSM settings. Inputs are collected only for requested output after extraction/prepare, before the next game update. Mesh resource identity is held with RuntimeScene's existing mesh data, not in a second entity-state index. Metadata does **not** claim measured adaptive LOD bias, page residency, GPU duration, or shadow-work/LOD diagnostics.

The reference uses `inverse(View * Projection)` from that same capture and normal pixel centers. Do not subtract jitter again: it is already represented by the submitted projection. A canceled frame can advance the scene index without advancing submitted-frame/jitter history.

## Independent reference and observation

`DefaultGameRayShadowTests.DefaultSceneShadowsMatchHardwareRayTracing` retains the actual default moving-sphere/static-imported behavior, TAA, async compute and available mesh/mixed raster. Beauty and visibility captures each retain raw color and their frame metadata; the test requires matching camera/projection/jitter/time/transforms/lights/settings across those runs. Sphere rays use captured transforms, removing the duplicated motion/seed reconstruction. Static imported source instances must match the captured transform multiset before using captured matrices. This is a static-scene consistency check, not support for arbitrary moving imported-node remapping.

The directional numerical input is `vsm-shadow.rgba16`, not `vsm-shadow.bmp`. The explicit visibility material continues to interpret user vertices, alpha UVs and normal texture itself; production source is not edited or string-replaced. Its mapped-normal shading condition is reported independently, not used to remove a geometric receiver from comparison.

Independent rays use original glTF triangles, source alpha masks/UVs, barycentric geometric position and geometric-normal ULP origin. They do not inherit production normal/depth bias or mapped-normal eligibility. Geometric light-facing eligibility now uses the physical positive hemisphere rather than the previous arbitrary 0.15 facing cutoff. Local-light truth includes authored range and spot outer cone. Point rays report source-space dominant-axis cube faces separately. No production page-table/depth/HiZ data enters truth.

`RayShadowComparison` keeps every eligible source receiver, including boundaries and missing rendered receivers. It retains continuous absolute error, aggregate/boundary/interior populations, mean/max error, lit/false-shadow and shadowed/missed fractions, coverage, shading rejections, self-hits, range/cone rejections and cube-face populations. Nonfinite visibility is a full mismatch/error, never silently accepted. CSV and actual/expected/error/coverage/shading images are retained; grayscale BMPs also have PNG companions for behavior-check evidence.

**Unchanged #132 gates:** pixel error > .15 is mismatch; mismatch and mean error each <=2%; false-shadow/lit and missed-shadow/shadowed each <=2%; rendered receiver coverage >=98%; >=32 shadowed and >=512 lit receivers; zero unexpected self-hit. No edge-exclusion denominator is introduced.

## New local-light fixtures (initial preparation; later validated in the GPU record)

`LocalLightRayShadowTests.LocalLightShadowsMatchIndependentSourceTriangles` has four authored rows: `point`, `spot`, `point-range`, `spot-cone`. The range/cone rows require independent rejected populations; point rows require receivers on at least two cube faces. At the initial checkpoint these were **source counts**, not discovery/execution/pass counts. Later actual discovery and six final isolated passes are recorded in the linked GPU validation.

`LocalLightRayShadowTests.LocalLightPagePressureRecoversAfterMovingCasterAndCanceledRefresh` has point and spot rows. Public World content supplies SpotLight directly; no Spot edit API is added. A 64-page configured atlas, moving caster, public refresh and canceled frame are followed by final raw capture. The old frame-31 footprint and recovered frame-63 footprint each get the same independent gates; final scene/submitted indices must be 63/62. The unshadowed and ambient controls cancel the corresponding frame too, preserving projection/jitter alignment.

These fixtures observe **Standard user-material linear color**, taking `(shadowed - ambient)/(unshadowed - ambient)` for an isolated light. They do not patch production shader text. No radiance threshold filters out receivers: nonpositive direct radiance produces an indeterminate visibility and full error. The later authorized GPU run did expose Half quantization at the range boundary; the fixture uses stronger authored intensity there without filtering receivers or changing gates. Color ratios are validated for the six retained fixtures, not arbitrary shading. This is still not a direct point/spot visibility-mask material. The independently authored light/triangle truth does not by itself prove complete numerical acceptance.

Configured page pressure is **not** proof of a specific fine-page miss, coarse fallback or no-resident-mip transition. Those exact resident-state cases, all six cube faces, backend behavior, alpha/custom-deformation coverage, and #135's final sampling semantics remain integration work. Existing `DefaultGameShadowReferenceTests` remains diagnostic (its legacy beauty/CPU comparator has edge filtering and fixed interpolated-normal offset); it is not promoted to acceptance by these changes.

## Actual validation

SDK 10.0.112; executed CPU TRX reports .NET runtime 10.0.12. The owned checkout initially lacked `external/Alimer.Bindings`; `git submodule update --init external/Alimer.Bindings` installed the pinned `8d15f4e182f77df0c9af3aaf7ccd5dd8aeafc2a4` dependency in this checkout only. No historical/delivered/integration/main content was cooked or changed.

1. Before parser implementation, `FrameOutputTests.SelectedLinearOutputIsAcceptedWithoutChangingDefaultRendering`: **1 executed / 0 passed / 1 failed / 0 skipped**. The public parser ignored both new flags and returned the same options as the ordinary run. This is an actual **CPU public-output red**, not an observed shadow/GPU failure.
2. After implementation, filtered `FrameOutputTests`: **5 executed / 5 passed / 0 failed / 0 skipped**. Tests cover selected output/options, last-frame selection, HDR Half/frame-data retention, and rejected negative/out-of-range selections.
3. Release Cluster and Runtime test builds both exited 0, each with **0 warnings / 0 errors**. These are preparation, not tests/discovery/rendering; final logs retain their actual elapsed times.
4. Default `Check-TestRules.ps1`: **24 projects / 0 violations**. An earlier 180-second command wrapper expired partway through; the later completed run, with a 600-second build/check bound, is the rule result. Neither command executed GPU tests.
5. `ReferenceShaderTests.IndependentSourceRayShaderCompilesWithoutGraphicsDevice` is compiled but **not executed**: its source-compilation request was guard-denied. No shader compile pass/failure is claimed.

Durable evidence: `artifacts/acceptance/reference-125/` (CPU red/green TRX, logs, rule output). Temporary top-level logs are moved there rather than left as probes.

Commands used, from the owned tree:

```powershell
git submodule update --init external/Alimer.Bindings
# Before implementation: isolated assembly then contained only the one public parser test.
dotnet test tests/SomeEngine.Runtime.Output.Tests/SomeEngine.Runtime.Output.Tests.csproj -c Release --results-directory artifacts/acceptance/reference-125/cpu-red --logger 'trx;LogFileName=output-option.trx'
# Actual final device-free output selection; excludes the unexecuted source compiler.
dotnet test tests/SomeEngine.Runtime.Output.Tests/SomeEngine.Runtime.Output.Tests.csproj -c Release --filter FullyQualifiedName~FrameOutputTests --results-directory artifacts/acceptance/reference-125/cpu-green --logger 'trx;LogFileName=linear-output.trx'
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release -v minimal
pwsh -NoProfile -File tools/Check-TestRules.ps1 -ResultsDirectory artifacts/acceptance/reference-125/rules
git diff --check
```

## Next authorization and acceptance gates

At the initial checkpoint GPU was reserved to #134 and only CPU checks/builds had run. A later exclusive grant permitted the public selected-frame capture and six local RT cases in the linked GPU record; no three-scene measurement ran and GPU is now released. Prior denials of listing, issue/source inspection and standalone CPU shader compilation were not retried or worked around. Format/CopySource/readback compatibility is now verified by public capture output, not denied source inspection. The separate denied CPU/SPIR-V compiler test remains unexecuted; any future attempt still requires explicit source authorization.

After coordinator authorization, cook owned, source-compatible default content, freeze hashes and source, then run in serial:

```powershell
# CPU source compiler: requires renewed authorization for the named reference shader source.
dotnet test tests/SomeEngine.Runtime.Output.Tests/SomeEngine.Runtime.Output.Tests.csproj -c Release --filter FullyQualifiedName~ReferenceShaderTests
# Hardware discovery belongs inside the serialized GPU window, including when filters are supplied.
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --list-tests --filter FullyQualifiedName~LocalLightRayShadowTests
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~LocalLightRayShadowTests.LocalLightShadowsMatchIndependentSourceTriangles --results-directory <owned-proof> --logger 'trx;LogFileName=local-lights.trx'
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~LocalLightRayShadowTests.LocalLightPagePressureRecoversAfterMovingCasterAndCanceledRefresh --results-directory <owned-proof> --logger 'trx;LogFileName=local-recovery.trx'
```

Set and restore `SOMEENGINE_SHADOW_EVIDENCE`, `SOMEENGINE_SHADOW_SOURCE` and `SOMEENGINE_TEST_ARTIFACTS` around the actual hardware run. Do not call authored row counts executed tests. First establish raw copy/format/readback and selected-frame behavior, then evaluate the local observations; do not weaken thresholds to hide radiance-ratio or production errors.

The final wrong-version numerical RT red and post-#135 default three-scene RT/beauty/backend/performance runs remain integration gates. This parser red is not their replacement. No issue is closed, pushed or given a PR by this slice. Keep #70/#81/#125/#127/#132 open until their own criteria are met.
