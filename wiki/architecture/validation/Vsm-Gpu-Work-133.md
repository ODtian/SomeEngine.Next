# Actual GPU target work and deferred lifetime — #133

Scope: [#133](https://github.com/ODtian/SomeEngine.Next/issues/133), parent [#132](https://github.com/ODtian/SomeEngine.Next/issues/132), implement-spec [#70](https://github.com/ODtian/SomeEngine.Next/issues/70). This is a source/shader and targeted-behavior checkpoint, **not final #125 quality or #127 performance acceptance**.

## Base and recovery

Owned branch `agent/rg70-ticket-133` was created at exactly `07d9462865f16049729a7a755d218247c9a373ed`; HEAD equality and empty status were checked before editing. Neither the dirty main tree nor historical ticket-127 tree was edited, reset or cleaned.

- `f97c4e5f`: immutable `7507ba59` independent-reference/coarse-sampling corrections.
- `9e5061c4f5d34702edd48ddee461ecc0ad255f5e`: reconstructed individual-view, correct-but-slow source/content checkpoint. See [recovery provenance](Vsm-Work-Recovery-133.md) for exact selections, exclusions, hashes and limits. It is not a byte-identical immutable snapshot of the historical bounded-batch numerical pass.
- `bdeb3b5e`: implementation source/shader checkpoint and concentrated verification artifacts.
- This slice removes that checkpoint's CPU source/view copied chains and deferred-only root rediscovery. RenderGraph and source-range-only BVH/residency additions are restored to the authorized integration base. `git diff 07d94628 -- src/SomeEngine.RenderGraph` is empty on the delivered source, preserving the #82 failure behavior.

No new live private EpicGames/UnrealEngine retrieval or recursive discovery under historical `rt-final-three-scenes` was attempted. Retained citations and separately identified bounded-batch evidence remain historical evidence, not fresh acceptance.

## Delivered data flow

`ClusterPipelineSystem.Passes.cs` installs exactly one target main geometry region and one target post-HiZ region. Material bins and hierarchy levels remain persistent graph maps; source clusters, instance rows and maximum shadow-view capacity no longer multiply CPU-created traversal/cull/bin/raster chains.

1. VSM compacts actual uncached view/mip pairs. The resulting GPU count and indirect dispatch seed actual instances in groups of 64 and actual views in groups of 32.
2. GPU traversal alternates bounded frontiers, rejects frustum/sample-free work, chooses geometry using the original unjittered geometry view, requests missing resident pages, and emits individual-view 16-byte candidates.
3. Only a real previous-HiZ rejection enters deferred work: an instance/root or hierarchy node records its node/instance/view identity; a cluster records its request and coarse-geometry choice. Missing history is not a deferred reason.
4. Target deferred entries, count and indirect arguments have distinct buffers from primary deferred work and from the traversal/candidate scratch. The target main reset initializes them once. Intermediate target HiZ and scratch resets do not erase them.
5. The target post region starts with `ResumeDeferredClusterWork`, consumes that retained prefix, tests current HiZ, and appends its visible work after target main work. It does **not** run a root seed or repeat full-source discovery.

The existing material/shader interpretation, per-view identity, mesh and vertex raster routes, software route, tessellation and streaming feedback remain. No target candidate view-mask experiment or mask-only raster route was imported. The intermediate hierarchy's 32-view rejection mask is not a raster candidate mask.

## Width and execution failure

The old CPU `ulong` source totals/ranges and shader narrowing are gone, rather than narrowed into a replacement global product. GPU queue counters retain only actual bounded work. Saturating compare/exchange reservations keep each count in `[0, capacity]`, even if rejected demand exceeds 32-bit aggregate arithmetic; rejected work cannot wrap a count back into writable entries. A wave's leaf offsets remain local, not source-wide aggregate offsets.

Node indirect dispatch and deferred node resume use portable two-dimensional dispatch: `x <= 65535`, `y = ceil(groups / 65535)`, with traversal flattening the group coordinates. Candidate/resume thread groups remain within the existing `64 * 65535` capacity limit. The public primary-view description and target context reject view counts exceeding the 19-bit per-stream identity space; they cannot alias the policy-selection bit.

A whole-frame four-byte GPU status records `Hierarchy`, `Candidates`, `Deferred` or `Visible` rejection. This is execution failure, not a silent LOD or caster reduction. `ClusterWorkOverflowException` exposes the flags and configured capacity. `SceneScript.ClusterWorkCapacity` configures the actual existing pipeline memory limit; there is no test-only queue mirror or inspection API.

DefaultGame collects completion readbacks and calls `ThrowIfWorkFailed` before delivering an image and before reporting final success, including asynchronous runs' last submission. VSM depth completion reads the same status and does not mark incomplete caster depth cached/complete. This guard is necessary interface coherence, **not** #134 retry/window policy or #135 HiZ/cache redesign.

## Red → green at approved public seams

Tests use only `Application.RunAsync`, `SceneScript`, `SceneImage`, public output descriptions and public distinguishable errors.

- `InsufficientGeometryWorkMemoryFailsInsteadOfPresentingAnIncompleteScene`: capacity 1 previously completed four frames with `Failure.None`; red expected `Rendering`. Green checks a typed overflow with capacity 1 and nonzero flags; an incomplete frame is not delivered to its requested image callback.
- `View_counts_that_cannot_retain_distinct_geometry_identities_are_rejected`: 524289 primary views previously constructed successfully; red expected `ArgumentOutOfRangeException`. Green rejects it and accepts the 524288 boundary.
- Coarse directional caster-motion is carried forward through public scene output, not the #82-deleted legacy driver. A small atlas and a single coarse-enabled directional clipmap exercise fallback demand. Whole images after moving the caster match a fresh final-state arrival, including both old and new footprints; shadowed output must differ from no-VSM output. No edge exclusions.
- A separate subdivision-five streamed caster outside the camera frustum still shadows the visible receiver. Deleting it leaves no-VSM beauty identical but removes its center shadow. The public-run log additionally reports 10/12 resident pages with VSM versus 7/12 without it; the test does not assert queue/storage state. This exercises target-driven demand for geometry not contributing to primary visibility.
- Rear caster same-frame recovery compares history with a moved front caster against an independently started final scene. Canceled caster frames likewise resume to the fresh-arrival output. These are history/lifetime checks, not independent ray-traced accuracy claims.

A temporary missing-post mutation was rejected by the tool risk gate. No mutation was applied, no workaround was attempted, and no mutation-sensitivity result is claimed. The first two tests have actual observed pre-implementation reds; the new image regressions are compatibility checks on the new path.

## Reproducible content

From this checkout (PowerShell; own tree only):

```powershell
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project "$PWD/templates/DefaultProject" --engine "$PWD"
dotnet build templates/DefaultProject/Game.App/Game.App.csproj -c Release -v quiet
```

The content build completed all 69 assets. No historical binary/shader payload was imported. Import records are regenerated; the two newly introduced VSM shader import records are retained explicitly. Generated binary assets and catalog remain build products, not a substitute source snapshot.

SHA-256 of the checked generation:

| Item | SHA-256 |
| --- | --- |
| `cluster_bvh_traverse_core.slang` | `9D9C2503E01C3152DD72F9134FB6D58749A26CC08CAB8A4D2B28A645402BAAB9` |
| `cluster_bvh_traverse_seed.slang` | `82C28713C62AC3EDDAD3ADF7F1EA6EFBEE95FEFA43910875B335D40D2E2C9BE6` |
| `cluster_cull.slang` | `863283D577297CC4BFD74231CA6693F63EB38471AB7608789C68AB3B0D1B28A9` |
| `cluster_deferred_resume.slang` | `68537A6E1B135555913F755FE744DAC8FFCE72A9D9845D694ECA5CE7F263BD69` |
| `cluster_work_status.slang` | `E8AB3B180D2F441C3FA7D2549870729E7F80D53AF4E447CD3255378DB3E61E96` |
| `vsm_page_depth_complete.slang` | `1F9908A5F42AD9A8D17AD5447FB7AB682E519A64B96A480B7855C0CF426C8E54` |
| `Library/AssetManifest/catalog.json` | `93FF32C9880B42483F0CF28244030590267E7A66A664B955CA2A6443CA25F928` |
| Default Cluster settings packet | `D00D7428EE0C0DD8D8ADC9D6963CB83BD0C58C27B2F01EF3415066AE837D2E97` |
| Default VSM settings packet | `489E1AFFE9D02874802B3B60540FC199B79DE5077BF581236E1CD956E1A70F68` |

The settings packets match the recovery checkpoint. Quality was not reduced to obtain these checks.

## Concentrated verification

Retained artifacts: [`artifacts/acceptance/ticket-133`](../../../artifacts/acceptance/ticket-133). All runs were in the owned tree, with parent authorization for content generation and targeted GPU/public rendering validation.

| Check | Result |
| --- | --- |
| Game.App Release build | 0 warnings, 0 errors |
| Default content + engine shader build | 69 assets completed |
| Public rendering batch | 40 discovered, 21 executed/passed, 0 failed, 19 capability-skipped |
| Separate off-camera streamed caster check | 2 discovered, 1 executed/passed, 0 failed, 1 capability-skipped |
| Full RenderGraph tests | 178 executed/passed, 0 failed/skipped |
| Public Cluster output tests | 3 executed/passed, 0 failed/skipped |
| DefaultGame 1024-sphere startup/output, 12 frames | passed with VSM, default TAA, three queues |
| `git diff --check` | passed |

All 20 rendering skips across the two batches are Vulkan: `NotSupportedException: The Vulkan adapter does not support required Device features: Presentation.` D3D12 receiver-mask cases include mesh, vertex/pixel and software routes at two normal biases. The batch also covers coarse/moving/rear/canceled caster shadows, camera scroll, camera occlusion recovery, cancellation, empty geometry, material replacement, tessellated material insertion/removal and streaming with detail not fitting at raster edge thresholds 4 and 64.

Commands for the retained tests:

```powershell
$filter = 'FullyQualifiedName~CoarseDirectionalShadowsFollow|FullyQualifiedName~MovingTheFrontCaster|FullyQualifiedName~CanceledCasterFrame|FullyQualifiedName~MovingLightShadowsMatch|FullyQualifiedName~DirectionalShadowsAfterCameraScroll|FullyQualifiedName~ReceiverMasksPreserveNormalOffsetShadows|FullyQualifiedName~StreamedGeometryKeepsTheSphereInterior|FullyQualifiedName~CameraMovementRecoversOccludedGeometry|FullyQualifiedName~CanceledGeometryFrame|FullyQualifiedName~DeletingAllGeometry|FullyQualifiedName~AddedTessellatedMaterial|FullyQualifiedName~RemovedTessellatedMaterial|FullyQualifiedName~ReplacedMaterialMatches|FullyQualifiedName~InsufficientGeometryWorkMemory|FullyQualifiedName~HardwareFrameLoopDeliversRequestedImages'
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter $filter --logger 'trx;LogFileName=public-rendering.trx' --results-directory artifacts/acceptance/ticket-133
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~StreamedCasterOutsideTheCamera --logger 'trx;LogFileName=off-camera-caster.trx' --results-directory artifacts/acceptance/ticket-133
dotnet test tests/SomeEngine.RenderGraph.Tests/SomeEngine.RenderGraph.Tests.csproj -c Release --logger 'trx;LogFileName=render-graph.trx' --results-directory artifacts/acceptance/ticket-133
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --filter FullyQualifiedName~ClusterPipelineOutputTests --logger 'trx;LogFileName=public-output.trx' --results-directory artifacts/acceptance/ticket-133
& ./templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe --test-scene "$PWD/templates/DefaultProject" --frames 12 --hidden-window --fixed-step 0.016666667 --no-vsync --no-debug-ui --vsm --verify-frame-output
```

DefaultGame reported 1280×720 output, hash `01B108E3118D6B42`, 36156 primary candidates, 19524 primary HW visible clusters, no tessellation drops/quality limits, 156 declared graph passes and 111 scheduled passes. Residency was healthy (`1/1` meshes, `3/6` pages, 3 missing, no queued/active loads/failures); this does not assert every detail page is required or resident. This finite startup check is **not** a convergence/performance measurement.

### Errors encountered and corrected

- Initial new C# compile: `renderGraph` was not the local name in `ExecuteFrame`; corrected to `graph`.
- First integrated shader run: WorkStatus was bound after target filter resources for traversal although its reflected UAV precedes them. Normal rendering failed with BufferSrv/BufferUav binding mismatch. Binding order was corrected; the capacity test was tightened to require the typed overflow rather than merely any rendering failure.
- Initial coarse test setup: nonexistent material name (`shadow caster`) and then a resolution bias selecting beyond the only available clipmap caused scene-action/no-shadow failures. The test now addresses its authored second mesh and uses valid receiver selection with constrained atlas fallback; no renderer behavior was changed to satisfy these setup errors.
- A hash command used a nonexistent root `content.catalog.json`; corrected to the actual `Library/AssetManifest/catalog.json` above.

No temporary scripts/probes were created. Generated test scene directories are disposed by their fixtures. Only required acceptance records are retained; no old evidence was changed.

## Handoff and remaining acceptance

- #134 still owns valid-page hardware-window work and capacity/retry policy. The retained rectangle-to-window expansion can now report a visible-work overflow; it does not silently publish success. This ticket does not promise final scene capacity or performance.
- #135 still owns packed-page HiZ indexing/copy cost and cache-state redesign. Whole previous-page HiZ copying and existing packed layout remain the before mechanism. The completion-status guard is the only new coherence seam here.
- Final independent full-receiver RT/default beauty validation, point/spot/fallback/custom-deformation coverage, final backend coverage, and wrong-version sensitivity on the final combined source remain #125/#127 obligations.
- No final three-scene performance, calibrated GPU timing or final before/after CPU trace was run. Historical bounded-batch passes and timings are not acceptance of this source. #134/#135 must land before that feedback round.
