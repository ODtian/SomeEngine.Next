# Draw-valid windows and bounded raster work — #134

Scope: [#134](https://github.com/ODtian/SomeEngine.Next/issues/134), parent [#132](https://github.com/ODtian/SomeEngine.Next/issues/132), implement-spec [#70](https://github.com/ODtian/SomeEngine.Next/issues/70). **Source/shader and concentrated public-behavior acceptance, not final #125 RT accuracy or #127/#81 performance acceptance.**

## Version and ownership

- Owned branch `agent/rg70-ticket-134`, tree `F:/SomeEngine.AgentWork/rg70-remaining/trees/ticket-134`.
- Before edits: clean HEAD exactly `35295fbfb429a451fb0f591fde84f76839bb9414`; requested base and current integration tip were identical. The base-ancestor check passed.
- Main implementation/content-source checkpoint: **`eef0934596d9f2c13918a9d8b6842b514404b273`**. Final production source: **`b82a9dc9e5dba88394b2b20b84cdae93cb1b042c`**, whose only follow-up change declares atomic status updates as partial writes (the frame reset remains a complete write). Current integration was merged before the final checks; Git reported `Already up to date` at the same integration tip.
- Neither integration, dirty main, old dirty ticket-127 nor delivered #133 was modified. No issues closed, pushes or PRs made.
- Directory listing and live GitHub issue retrieval were denied by the tool guard and not retried through another route. User requirements, named retained research/coordination, repository domain rules and actual delivered #133 interfaces supplied the implementation frontier. No new Epic private-source retrieval or denied `rt-final-three-scenes` traversal occurred.
- #125's parallel reference/capture work is not imported here: no changes to DefaultGameRayShadow*/RayShadow* or Runtime capture/frame metadata. Its raw pre-TAA/jitter-aligned independent-reference work remains separate.

See [#133 GPU work](Vsm-Gpu-Work-133.md) and [recovery provenance](Vsm-Work-Recovery-133.md). Historical trial results are not targets or accepted source for this ticket.

## Actual window path

`cluster_raster_target_resources.slang` now holds one draw-mapping resolver used by both cull filtering and draw/pixel output. It accepts a mapped, unlocked page with matching signed world-page tags and either current generation or generation zero. **Zero means dirty but drawable, not sample-valid.** Sampling keeps its completed-generation requirement; dirty newly allocated pages must be drawable before completion.

`ClusterTargetPolicyRasterBoundsWindow` intersects the conservative projection with the uncached page rectangle, request bits, drawable mappings and receiver coverage. `AppendVisible` partitions HW work on the four-page grid, including small rectangles crossing a partition boundary. Each partition repeats those eligibility tests against the caster's projected pixel bounds, crops to surviving pages and emits nothing when none survive. It no longer treats hierarchy bits alone as sufficient or bypasses checks for small rectangles.

A cropped rectangle may contain holes. The original final page request/mapping/receiver-mask and user visibility/alpha evaluation remain authoritative per pixel. No receiver is removed to make image comparisons pass. The resolver's world tags retain toroidal clipmap/mip semantics.

When a material does not provide conservative culling bounds, the target uses full-view bounds rather than accidentally clipping evaluated geometry to the original undeformed cluster bounds. Its original-level geometry choice remains unchanged. User `IVertexEvaluate`, `IVisibilityEvaluate` and domain evaluation still interpret geometry/materials per view; no fixed vertex layout or view-mask raster route was introduced.

### Coherent four-page consumers

- `vsm_view_compact.slang`: `RasterWindowPages = 4`.
- `VirtualShadowFeature`: maximum partition count uses ceil(pages/4); raster context width/height are `PageSize * 4`.
- Existing policy viewport/origin/remapping, scratch attachment sizing, mesh/vertex raster and tessellation consumers all derive dimensions from that target/context.
- Default 128 px pages therefore use a **512×512 px HW viewport**. Configurable supported page sizes remain supported.
- The existing 8×8 groupshared page cache is still a cache-capacity bound, not the raster-window size.
- SW keeps one bounded geometry item with its target scissor; it is not counted as an HW page window.

Retained reference only: commit `396c9f059903aed5fec78ecd3d437a40c6415368`, recorded UE 5.8.3, [NaniteClusterCulling.usf:875–940](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteClusterCulling.usf#L875-L940) for valid mapped-page/nonempty windows, and retained D03 basename-only `VirtualShadowMapDefinitions.h:13–21` for 128 px/four-page semantics. No missing full header path or live verification is asserted. [NaniteRasterBinning.usf:184–235](https://github.com/EpicGames/UnrealEngine/blob/396c9f059903aed5fec78ecd3d437a40c6415368/Engine/Shaders/Private/Nanite/NaniteRasterBinning.usf#L184-L235) distinguishes pre-bin output from material execution work. UE capacity clipping does not satisfy this engine's completeness requirement.

## One failure holder and bounded expansion

This ticket selects **distinguishable functional failure**, not split/retry. No buffer capacity was enlarged as a fix, no geometry/resolution/features reduced, and no CPU conservative copied chains restored.

- #133's whole-frame `WorkStatus` remains the only geometry-completeness holder. `Visible` rejects required window pressure. New `RasterBatches` and `Tessellation` flags extend that same public `ClusterWorkOverflow`/`ClusterWorkOverflowException` result.
- Raster generation permits at most nine material/vertex-reuse batches per visible item, matching the existing allocation. Unsupported expansion records `RasterBatches`; it cannot silently write into another item's region. Direct writes and prefix scatter retain actual index-buffer bounds checks.
- Prefix reservation is saturating. A wave that cannot retain its range writes zero indirect work rather than executing an unretained range. Count and scatter repeat the same bounded expansion; no indirect consumer is authorized by an overflowing raw total.
- Surface root, child-level and final-patch reservations are saturating. Rejected suffixes set the same frame status, in addition to existing diagnostic drop counts. Capacity failure and quality-limited refinement remain different outcomes.
- Existing VSM `CompleteVirtualShadowPageDepth` reads this status and refuses to stamp incomplete depth complete. Runtime still calls `ThrowIfWorkFailed` after completed readbacks, before image delivery/final success. No truncated dirty page is made sample-valid merely because raster submission finished.
- Allocation pressure/missing mappings still belong to VSM's allocation/fallback evidence; no mapped geometry output is invented for an unmapped page.

### Width and indirect proof (source, not measured demand)

Public capacity remains `64 * 65535 = 4,194,240`. Accepted visible indices remain below 2^22; raster/tessellation packed indices are unchanged. Nine-batch expansion is bounded by **37,748,160 entries per phase**, well below uint. Bin dispatch has x≤65535, y=ceil(count/65535)≤577; flattened work indices and the rounded rectangle remain below uint. Tessellation level/final pools retain the candidate bound; split groups remain ≤65535. CPU resource byte sizes/argument offsets keep checked ulong arithmetic.

The direct path reserves `floor(capacity/binCount)*9` entries per bin only when the phase's visible count fits that region. Its SW prefix is at most `SWVisible*9`; HW follows that prefix. Together they cannot exceed the region. The prefix path reserves at most nine times accepted visible work. The explicit nine-batch guard makes this bound enforced even where imported batch encoding alone would not prove it; it is not a claim that every imaginable material encoding fits nine.

The old source-range uint narrowing is not recreated: #133 removed that representation. Target page coordinates retain the existing supported ≤32768 pages/row bound. No new view identity packing was added.

## Units and adaptive quality

`CaptureFrameMetrics().Work` carries completed-frame semantic evidence, copied from the same GPU status holder into the existing metrics readback. Tests do not inspect queues, packed windows, pass ordering or storage. It distinguishes:

1. Target candidate **tests**, including main/post-HiZ attempts, not unique source geometry.
2. Potential HW partitions, rejected-empty partitions, required draw-valid windows and accepted windows.
3. Required/accepted bounded SW geometry items.
4. HW/SW post-material raster batches across primary and target phases.
5. Required surface roots and final patches, distinct from existing accepted/drop/quality-limited surface metrics.
6. Whole-frame rejection flags. `Work.Complete` means geometry work was not rejected; it does **not** mean all requested pages were allocated, history is valid or every page is sample-ready.

Optional totals use paired uint atomics for 64-bit completed-frame counts, without requiring buffer-int64 atomics. They are not consumed until completion. Diagnostics are allocated in the status buffer only when frame metrics are enabled; ordinary failure-only operation still uses four bytes.

Adaptive VSM work feedback now counts HW **cropped page-batch area**, with each SW batch weighted 64. `DynamicResolutionTargetRasterWork` retains 8×8-page material-batch equivalents: divide area by 64. Splitting one rectangle into four-page partitions alone cannot raise feedback pressure and secretly lower resolution. Compared with the old one-per-window weighting, cropping can lower pressure and restore more detail. This is an explicit feedback-unit change, **not proof of equal effective quality or a speedup**; final comparisons must record effective bias/work/coverage. The configured budgets and default resolution/geometry/features are unchanged.

Observed first-frame sparse-receiver diagnostics, both headless D3D12 and Vulkan: 13 target candidate tests, 8 potential/required/accepted HW windows, 7 required/accepted SW items, 30 HW and 7 SW material batches, no rejection. The constrained capacity gives the same result. These runs reported **zero empty partitions**; do not claim measured empty-work savings from them. The no-empty-output property is supported here by the eligibility/emission source path and whole-output regressions, not a measured count reduction. Settled cached frames report zero target candidate/window work; primary raster work remains distinct.

## Public red → green and compatibility

Approved seams: `Application.RunAsync` / `SceneScript` / `SceneImage`, and existing public world/rendering/graph fixture outputs and distinguishable failures. No friends, internal probes, queue assertions, message assertions or sleeps were added.

- **Actual red:** on base production source, the surface-work test with capacity 4096 expected Rendering failure but got `ApplicationFailure.None`. Source candidates fitted while downstream tessellation could truncate. The earlier capacity-64 exploratory run failed at `Candidates` and was not a tessellation red.
- **Green:** the same public run now exposes exactly `ClusterWorkOverflow.Tessellation`, capacity 4096, and no requested incomplete image. Final rendering batch reran it on the combined source. No mutation was performed or claimed.
- Sparse receivers plus an off-camera broad caster: first changed and settled whole images match a fresh final-state run without receiver masking. Both old/new footprints and receiver edges are retained; before/after and no-VSM images must differ. Default mesh/mixed, forced vertex/pixel and SW-capable mixed routes are covered. This is state-transition/coverage parity, not independent numerical RT accuracy.
- Supported 4096 versus 16384 work memory: same sparse receiver/caster geometry, first and settled full material color/depth, completed geometry result. No test assertion freezes diagnostic count values; counts are retained as production evidence only.
- Fully transparent user visibility matches removing only the added caster, preserving existing receiver geometry and shadows. D3D12/Vulkan, HW-only and mixed routes pass. This is an analytic transparent-caster reference; it does not claim patterned-alpha/full-scene RT acceptance.
- Eight existing material-replacement cases cover ordinary and tessellated user stages, both backends and HW/mixed paths. Existing runtime checks retain coarse/moving/rear/off-camera streamed caster shadows, receiver-mask parity and directional scroll.

## Reproduction and retained results

All commands ran in the owned tree, .NET SDK 10.0.112. GPU use was authorized exclusively for this implementation. Runtime fixture content is source-generated and disposed by the fixture; no historical cooked shaders were used. Default content build completed **69 assets**. Compatible import records are committed; generated binary assets/catalog are build products reproducible from this checkout.

```powershell
Set-Location F:/SomeEngine.AgentWork/rg70-remaining/trees/ticket-134
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project "$PWD/templates/DefaultProject" --engine "$PWD"
# Red and first green used the same filter on their respective source slices:
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~InsufficientSurfaceWorkMemory --logger 'trx;LogFileName=surface-pressure-red.trx' --results-directory artifacts/acceptance/ticket-134
# Green logger: surface-pressure-green.trx
$filter = 'FullyQualifiedName~VsmWindowImageTests|FullyQualifiedName~RasterWorkFailureTests|FullyQualifiedName~ReceiverMasksPreserveNormalOffsetShadows|FullyQualifiedName~CoarseDirectionalShadowsFollow|FullyQualifiedName~MovingTheFrontCaster|FullyQualifiedName~StreamedCasterOutsideTheCamera|FullyQualifiedName~AddedTessellatedMaterial|FullyQualifiedName~DirectionalShadowsAfterCameraScroll'
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter $filter --logger 'trx;LogFileName=public-rendering.trx' --results-directory artifacts/acceptance/ticket-134
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --filter 'FullyQualifiedName~VsmWorkRenderingTests|FullyQualifiedName~ClusterPipelineOutputTests' --logger 'trx;LogFileName=work-and-alpha-green.trx' --results-directory artifacts/acceptance/ticket-134
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --no-build --filter FullyQualifiedName~ReplacingRasterMaterialMatchesFreshInstallation --logger 'trx;LogFileName=material-routing.trx' --results-directory artifacts/acceptance/ticket-134
& ./templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe --test-scene "$PWD/templates/DefaultProject" --frames 12 --hidden-window --fixed-step 0.016666667 --no-vsync --no-debug-ui --vsm --verify-frame-output
# Declaration-only final follow-up, after commit/merging current integration:
dotnet test tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release --filter FullyQualifiedName~SupportedWorkMemoryPreservesAllShadowedPixels --logger 'trx;LogFileName=final-work-status.trx' --results-directory artifacts/acceptance/ticket-134
dotnet test tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release --filter FullyQualifiedName~InsufficientSurfaceWorkMemory --logger 'trx;LogFileName=final-pressure.trx' --results-directory artifacts/acceptance/ticket-134
# Final default checks/builds:
pwsh -NoProfile -File tools/Check-TestRules.ps1 -ResultsDirectory artifacts/acceptance/ticket-134/rules
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release -v minimal
dotnet test tests/SomeEngine.RenderGraph.Tests/SomeEngine.RenderGraph.Tests.csproj -c Release --logger 'trx;LogFileName=render-graph.trx' --results-directory artifacts/acceptance/ticket-134
git diff --check
```

[Retained acceptance files](../../../artifacts/acceptance/ticket-134) include exact console commands' output, TRX, builds, SDK, source/content hash manifest and startup log. Discovery counts below are actual result rows, not source-theory estimates; no separate list-tests command was run.

The main concentrated records below use `eef09345` source/shaders. The final declaration-only `b82a9dc9` follow-up reran the completed target-work and typed pressure seams; shader/content hashes are unchanged.

| Main concentrated record | Discovered | Executed/passed | Failed | Skipped |
| --- | ---: | ---: | ---: | ---: |
| public-rendering.trx | 30 | 15 | 0 | 15 |
| work-and-alpha-green.trx (6 rendering + 3 public output) | 9 | 9 | 0 | 0 |
| material-routing.trx | 8 | 8 | 0 | 0 |
| render-graph.trx | 178 | 178 | 0 | 0 |
| **Total, these four main records** | **225** | **210** | **0** | **15** |
| final-work-status.trx, b82a9dc9 (repeated work-memory cases) | 2 | 2 | 0 | 0 |
| final-pressure.trx, b82a9dc9 (repeated surface-pressure cases) | 2 | 1 | 0 | 1 |

The 15 Runtime skips are Vulkan `Presentation` capability failures; the separate headless Vulkan rendering passes are real passes and do not establish Vulkan presentation support. Early surface red: 2 discovered, 1 executed/failed, 1 capability-skip. First surface green: 2 discovered, 1 executed/passed, 1 capability-skip; these are separate, repeated observations, not added to final unique scope.

Final Cluster/Runtime Release builds: each 0 warnings/0 errors. Default rules: 23 projects, 0 violations. `git diff --check`: passed. DefaultGame: 12 completed frames, 1280×720, 1024 spheres, TAA/VSM/default available mesh/mixed and three queues; output hash `F06B787A5C861667`, no surface drops/limits. This finite startup is not convergence, GPU timing or throughput acceptance.

### Errors and non-acceptance attempts

- First build could not find the uninitialized Alimer.Bindings submodule. Initialized only the owned submodule at pinned `8d15f4e182f77df0c9af3aaf7ccd5dd8aeafc2a4`; this was a build failure, not a behavioral red.
- Shader cook first failed because a shared, unused bin helper referenced the work rejection function in shaders without that status. Changed the helper to return failure and let its actual scatter caller record the status. Subsequent complete cook passed.
- Initial diagnostic allocation needed an explicit checked ulong conversion; corrected before the rendering checks.
- A combined final CPU-check shell hit its 300-second tool timeout after the rule log reached 23 projects. Its uncaptured overall exit was not promoted to a pass. Isolated final rule check (600-second hang guard) and separate Cluster/Runtime builds subsequently returned exit 0. No GPU/performance timeout was reclassified as success.
- Initial alpha setup failed 4 cases (5 other cases passed) because the existing fixture always authored texture alpha 255, ignoring the supplied Vector4 alpha. The fixture now authors the requested alpha; renderer behavior was not changed to satisfy this setup error. Final rerun passed all 9 cases.
- An initial settled-only work-memory run passed 2/2 but reported cached zero target work; it was superseded by first-frame plus sparse-receiver evidence. A requested cleanup of these own non-acceptance files, alpha setup-failure files and generated per-project rule logs was denied because the guard classified them as acceptance evidence. No deletion workaround was attempted. They are retained as explicitly superseded/non-acceptance history, not added to the final pass totals. No historical evidence or another worker's files were removed.

### Checked generation hashes (SHA-256)

| Item | Hash |
| --- | --- |
| `cluster_cull.slang` | `01DDE8D847848DD5BFA666F07BD4DBC8A809E17C43E3C9E55637AD498438410D` |
| `cluster_work_status.slang` | `E30C3735A446F9FFCF9F2C0497F623BE7CDED6B71924A8BC5432D11A5B0805DA` |
| `cluster_tessellation_core.slang` | `1C31811F3A62F05C7026BA5CC1E11EF0DBA8AE76E51B5BFFCD8DE9732D95E03B` |
| Default content catalog | `36DA4D7B3C1D22BBD8A77613F91B493D23112814F748418B342AA16BC5B1C450` |
| Source sphere model | `40523D69E4E2C6FC1316114008B3DE26100108A3A5ED2A5601FF1B9FF6C89A83` |
| Game.App runtimeconfig | `1AE3FC161EB583DBC3ACFF1A40549EFD71C6350290C1379D8BE7C006AB0626B3` |

## #135 handoff / limits

1. Consume the existing geometry `WorkStatus` at byte zero; it now includes material and surface failure as well as traversal/visible rejection. Do not mirror completeness in a second CPU/GPU path.
2. Draw mapping generation zero is deliberate. Completion and sampling must remain separate, and no required dirty page may become complete on a failure flag. Allocation failure and streaming-selected coarse geometry are separate from work overflow.
3. #135 still owns packed-page HiZ indexing/history copying, dirty masked-page coverage/cache-state redesign and page/sample fallback/recovery completeness. Current whole-history copying is untouched. The status guard does not prove those states correct.
4. Retain per-view material/geometry interpretation, main/post lifetimes, streaming and persistent graph. No CPU source/view copied chains or parallel target renderer were introduced here.
5. No final independent raw/jitter-aligned full-receiver RT comparison was run. Current default BMP/post-TAA numerical reference is not aligned truth. #125's parallel source/capture fix, point/spot/range/cone, patterned alpha/deformation, page-miss/recovery and final combined wrong-version sensitivity remain broader gates.
6. No final three-scene performance/trace/calibrated GPU measurement or cost reduction is claimed. Four-page area math and smaller viewport are source facts, not milliseconds. Adaptive-feedback effective quality must be frozen/recorded on both before/final sides after #135; historical trial names/settings are not accepted comparison targets.
