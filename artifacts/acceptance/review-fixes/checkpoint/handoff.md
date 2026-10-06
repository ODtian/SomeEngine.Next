# Combined review fixes — measurable checkpoint / GPU RELEASED

**Executable/source checkpoint: `833cab9158db4ceee79fa8f1ca613a8616472cbd`. This is NOT completion of all review findings or performance acceptance.** Parent/#81 can use its exact833 measure-candidate; later evidence/docs commits do not change production, shaders, fixtures or settings. All own synchronous build/test commands have ended. No further GPU/device/discovery/tests/builds during the parent performance window. No throughput, trace or calibrated GPU measurement was run by this implementer.

## Branch, integration, protection

- Branch/tree: `agent/rg70-review-fixes`, `F:/SomeEngine.AgentWork/rg70-remaining/trees/review-fixes`.
- Before edits, both actual clean integration and new own tree were exactly `6706c6cd598068e9e39e5d02b7d8095f9834e2c9`, with empty porcelain status.
- Resumption checked existing c32bbabf and preserved all WIP. Integration remains clean6706. Ordinary `git merge --no-edit codex/render-graph-regions-70` returned Already up to date; integration ancestor check exit0. Only own branch/tree modified; dirty main/historical/other-agent trees untouched.
- Commits:908ea139/e8f4cc6d adopt exactly #81's9800/71ec public compiler/blob and required visibility-debug compatibility fixes;7e4f5c70 first coherent review corrections + CPU evidence;c32bbabf visibility-capture geometric-plane call;833cab91 quality capture + heterogeneous diced patches + public regressions/docs.
- #79/#80/#134 actually reopened by parent; their obligations and #81/#135 prerequisites remain open. No issue close/push/PR.
- Approved seams: ordinary Application/SceneTestPlatform/startup/images, public graph/RHI outputs and typed failures; device-free comparator/artifact output. No friends/private probes/new world-to-GPU path. One owned-root directory listing was freshly denied and not retried/worked around; specific task source/edits/builds/tests proceeded under continuing authorization. No authorization wait.
- Parent froze original Sponza/Bistro; #81 completed separate three-scene compatible imports/staging. This branch did not duplicate large-scene imports. Read `content-preparation-81/three-scene-cpu-ready.md`.

## Standards — each finding's current status

| Finding | Implemented/source proof | Actual validation / remaining |
|---|---|---|
| S1 P1 UI transient uploads | `ResourceAllocators.cs` UploadReadUsages includes Vertex/Index. Read-page classification, actual native usages, existing element/constant/shader alignments and graph frame completion/cancellation remain coherent. No independent UI buffer timeline. | Ordinary public startup with SceneTestPlatform, default visible debug window versus hidden-UI control: Release1 D3D12 pass/1 Vulkan Presentation skip; Debug1 pass/1 same skip. Earlier script-default-hides-UI and missing-content-root harness failures retained; not production reds. Vulkan presentation remains unproved. |
| S2 P2 failure artifacts | Shared `tests/Shared/ImageArtifacts.cs`: byte-exact HDR/depth/RGBA comparison, each image's dimensions, full-union diff PNG and raw bytes. Cluster assertions use it. `ShadowEvidence.cs` retains RT/legacy actual/expected/diff/raw/CSV under SOMEENGINE_TEST_ARTIFACTS or artifacts/test-results before deleting working content; optional successful evidence retention preserved. | Device-free HDR/depth/different-dimensions and failure-retention tests pass. Actual finite regression failures retained PNG/raw evidence. No numerical gates or receiver denominators weakened. |
| S3 P2 fixture modes | `ShadowReferenceScene.cs` mechanically serializes explicit camera/placements/motion/lights; test bodies supply actions/options. Legacy reference uses submitted transforms/projection rather than frame63 motion reconstruction. Local point/spot/range/cone inputs are explicit test-authored data. | Builds/default rules pass. Full legacy diagnostic/local-light rerun pending; legacy edge-excluded comparator remains diagnostic, never promoted to acceptance. |

Qualified smells are NOT blindly counted as hard blockers: separate direct-read and copy-write allocators still have distinct alignment/concurrency/preparation requirements in the same graph frame holder; lifecycle consolidation needs a bounded architecture change rather than conflating reservations. Saturating-counter consolidation and unused context knobs remain maintainability follow-ups, not asserted correctness failures. Primary source-holder/one graph/world path/completion boundaries were preserved.

## Spec — each finding's current status

| Finding | Implemented/source proof | Actual validation / remaining |
|---|---|---|
| S1 P1 finite target tessellation | `cluster_tessellation_core.slang` selects existing page-aware target policy prior/current HiZ instead of encoded primary texture layer. Feature binders/neutral bindings are coherent. Public `CLUSTER_TESSELLATION_BOUNDS` declaration permits an explicitly authored finite linear domain; default displaced domains remain unbounded. | Exact6706 tessellation core with new public fixture:1 executed/1 image failure. Corrected hardware paths passed; mixed paths then exposed a second real defect (heterogeneous patches sharing a single-window groupshared page cache). Dicing now uses the same requested-page/receiver-aware writer without that incompatible cache. First/history/caster-change whole-image comparison against ordinary original triangles: final5 executed/5 passed, D3D12 VS/MS/mixed and headless Vulkan VS/mixed. No culling or supported route disabled. |
| S2 P1 page dispatch capacity | `vsm_page_dispatch.slang` and compact/clear/HiZ/reduction split page count across X/Y and flatten tiles in Z; each consumer checks actual count/tail. No page count truncation, resolution decrease or capacity inflation. | Source/build/interface checks and default rendering pass. Actual65536+ demand still pending. IMPORTANT additional source-audit limitation: flattened tiles² can exceed portable Z for large accepted PageSize>=4096; defaultPage128 and the reviewedPage16/65536-page case are not affected. This finding is PARTIAL, not universally closed. General legal page-band/tile packing must be finished after feedback. |
| S3 P2 changed properties/direct production | `IRenderInstanceProducer.WriteChanged`, component/bundle and Cluster geometry split rows by property. Adjacent current/previous changes no longer cause Cluster producer to write counterparts. `RenderInstanceWriteSlice.GetWriteSpan<T>` is a real tightly packed producer capability; roots/expansions are computed into final graph mapping, with no stack/pool copy. Cancel/abandon/parallel rules retained. | Compile/source proof only for actual producer path. Public `frame.GetBufferUploadRanges` already exists, so no test-only counter/API was added. Adjacent-property actual upload-range/value/cancel/abandon regressions still pending. |
| S4 P2 raw comparator | Raw ratio/numerators and coverage checked finite before preview clamp; finite out-of-range continuous error retained, nonfinite full error; CSV/full receiver population unchanged. | Actual CPU old-clamp red1/1 failed; minimal green1/1 passed; expanded final device-free output/compiler/comparison/retention suite11/11 passed. |
| S5 P1 exact invalidity/recovery | Existing public Try sampling and cancellation production are preserved; no speculative native/GC/finalizer patch. | NOT COMPLETE. Exact missing-fine/coarse/no-mip/unfinished validity, first-resumed independent recovery without global refresh, all-six point faces, patterned alpha/in-place/deformation and partial-submit output recovery remain. Old settled/global-refresh pressure and baseline-green expansion are not sensitivity proof. New quality frame-cancellation test is diagnostic correspondence, not this sampler-state acceptance. |

Three inherited native aborts remain FAILED zero-result hosts. No native fix, native reliability or full #135 pass is claimed.

## Genuine corrected-score sphere red → green

Corrected comparator + old visibility capture call on7e4f5c70 reproduced the parent red EXACTLY:1 executed/1 failed,40.718478% mismatch/mean,41.60603% false/lit,0.22291574% missed/shadow,99.518406% coverage,0self-hit.

The fixture had omitted the public geometric receiver-plane argument and biased using mapped normal unlike Standard. c32bbabf matches Standard's triangle-geometric plane/bias call; it does not change independent truth, shader evaluation conditions, geometry/default features, thresholds or denominators. The c32 pass was revalidated on final833 with quality enabled and final cooked content:

- 1 executed/1 passed; both64-frame captures complete, scene/submitted63/63, same actual jitter/transforms.
- **135592 receivers retained:**4486 shadowed/131106 lit;2640 mismatches.
- mismatch AND mean **1.9470176%**; false/lit **1.45302275%**; missed/shadow **1.827909%**; coverage **99.518406%**;0 self-hit.
- Boundary2.7521288% remains included in aggregate, not filtered. Near-gate result retained without tuning.
- Capture-call mathematical fix, **not production VSM/native repair**. No native abort in these runs. Correctness walls are not GPU/performance timings.

## Opt-in semantic quality — usable surface and honest limits

`SceneFrame.Quality` carries completed current-frame shadow/geometry/residency results. `VirtualShadowFeature.EnableQualityCapture/CaptureQuality` reuse graph readbacks, including public bound-input range reads; ordinary uncaptured throughput never installs these readbacks. Requests use frame tags, frame cancellation drops delivery, future frames disable capture passes. Current `_frameResolutionBias`, not future `_dynamicResolutionBias`, supplies applied additions. No second policy-state mirror/graph path/GPU timeline.

Public quality-correspondence regression: null-result red1 D3D12 failure/1 Vulkan Presentation skip; implemented green1 pass/1 skip after canceled frame. Final sphere sidecar serialization succeeds: RenderingFrame64, Geometry.FrameIndex64, directional/local additions0/0, dirty267, allocation failures0, free3826, raster page-area361854, typed overflowNone, registered/published mesh1/1, resident geometry pages3/6. Full per-view/mip resolutions and completed whole/partial/unfinished populations/8x8 receiver coverage are retained.

**Limits:** raster units v2 are HW page area and each SW batch weight64; old scalar equality is NOT equal work. Per-mip populations are NOT per-pixel selected-mip histograms. Additionally current `RequestedPages` readback occurs after the producer's request-counter reset (observed0); do NOT interpret it as this frame's original demand or lack of requests. That capture point is a remaining diagnostic correction. Actual applied bias, decoded views/mip dimensions, completed coverage, dirty work, geometry work and residency remain usable. #81 must judge matching from semantic comparable fields/full images, not infer it from defaults/pressure alone. Observer-only BEFORE backport must not import production repairs. If matching cannot be established, report the technical quality gap instead of performance acceptance.

## Final actual checks on833 / evidence

- Own full source-compatible default cook:68 assets, exit0. Shader interfaces/default unbounded material declarations compiled. Generated own imports preserved in cook-metadata.diff then restored; cooked generation unchanged.
- Cluster Release build (includes Game.App):0 warnings/errors, exit0.
- Device-free output/compiler/comparator/retention:11 executed/11 passed/0 fail/0 skip.
- Default test-rule sweep:24 projects/0 violations/exit0.
- Final corrected-score sphere + quality:1 executed/1 passed.
- Final finite original-triangle first/history/movement five-row batch:5 executed/5 passed/0 failed/0 skipped.
- Earlier ordinary UI Release/Debug:each1 executed/1 passed plus1 Vulkan Presentation capability skip. Headless finite Vulkan passes do not imply presentation support.
- Source833 was clean before final GPU commands; integration merged/ancestor checked; post-validation porcelain empty. SDK10.0.112/runtime10.0.12; no forced GC/retry masking.

All current hardware/cook/build/check evidence is at `F:/SomeEngine.AgentWork/rg70-remaining/review-fixes-validation/`:
- `checkpoint/{cook.log,cook-metadata.diff,cluster-build.log,cpu-output.trx,rules.log,sphere-quality.trx,finite.trx,completed-quality.json,post-validation-files.csv}`.
- Final full raw/BMP/PNG/sidecar/CSV/JSON: `checkpoint/sphere/ray-shadow-spheres-default-b28e771c1d0544e8878145212a618d02`.
- Prior reds/greens:default-spheres-corrected-score-red/default-spheres-geometric-plane;quality-red/quality-green;finite-target-red/finite-target-green (mixed failures)/finite-target-dicing-green;ui-release-platform/ui-debug-platform. Early harness/compile failures retained too.
- CPU original red/green/logs already committed under `artifacts/acceptance/review-fixes`. Final small records are archived in the same area by the evidence/docs handoff commit; full raw captures/CSV remain external and retained.
- post-validation-files.csv is explicitly POST-run hashes, not invented pre-run freezing or byte-identity proof for older executables.

## Parent / #81 immediate action

GPU RELEASED; no commands outstanding. Exact executable-source checkpoint833 is available to the already-owned measure-candidate-81. Rebuild/cook/import/restage using that source (shader entries/dicing and bindings changed); keep identical parent-frozen original inputs and default quality/features. Run THREE default raw RT/beauty with the SAME corrected comparator/geometric-plane observation, then only genuinely matched/converged paired non-trace<=60s each and separately CPU/calibrated GPU diagnostics. Do not benchmark during any builds. No performance result exists from this branch yet.

This single implementer retains all remaining review obligations after feedback/window scheduling: general dispatch packing/boundary validation, request-count capture-point semantics, instance actual ranges/recovery, broader affected regressions, exact local validity/recovery/all-face/alpha/deformation/partial submission, qualified maintainability judgments and final combined review. These are technical/scope work, not authorization waits. Keep all parent criteria/issues open until their own actual gates pass.
