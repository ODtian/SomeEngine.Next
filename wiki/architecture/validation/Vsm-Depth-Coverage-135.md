# VSM HiZ lifetime and completed receiver depth — #135

Scope: [#135](https://github.com/ODtian/SomeEngine.Next/issues/135), parent [#132](https://github.com/ODtian/SomeEngine.Next/issues/132), implement-spec [#70](https://github.com/ODtian/SomeEngine.Next/issues/70). **CPU/source/content-build checkpoint only. No observed rendering red/green, RT acceptance, GPU timing or performance acceptance.**

## Version and access boundary

Owned tree `F:/SomeEngine.AgentWork/rg70-remaining/trees/ticket-135`, branch `agent/rg70-ticket-135`, started clean at exactly `1667bd94c435e91c60006e4c504d3ab2abc0f2da` (merged/closed #133 and #134). Before edits, HEAD equality, branch and empty porcelain status were checked. Production source/shaders: **`c8d427183ea2b15870bfa52b593bf6761b25eb3d`**. Checked source: **`fe15c1cd306e8765ebb45402ed151e7a192bc339`** (only subsequent change fixes an inherited D3D12 test's missing compiler-instance call). Regression-only commit **`d6085c7db41397db09c2e53cf143de5547fe88a3`** contains the new public image behavior on that unchanged production base; it is a candidate for the later matching baseline run, **not a recorded failing version**.

Directory listing of the owned root was rejected by the tool gate. It was stopped and reported, not retried or reproduced indirectly. Exact task-scoped project documentation/source reads, own edits and CPU builds were allowed. No private Epic retrieval, rejected historical RT discovery, reference-125 rejected source/CPU test/issue refresh retries, protected tree edits, issue changes, pushes or PRs occurred. The earlier research note is not the actual merged source; current candidate/status/window/page interfaces were read directly in this owned tree. #125 capture/reference/local-light files were neither changed nor recreated.

## Root cause and source changes

### One HiZ resource, three ordered consumers

Previously `VirtualShadowFeature` created a second persistent HiZ texture and dispatched a full-pool copy for every mip before target work, despite #133 retaining actual deferred work. This copy no longer supplies discovery or lifetime safety.

The actual installed Cluster target regions are ordered main → intermediate occlusion → post. Main traversal/cull/material/tessellation reads finish before the intermediate region overwrites dirty HiZ tiles. Post resumes #133's retained entries, reads current HiZ and rasterizes its accepted suffix. Final HiZ is updated after post raster and persists for next frame. Both SRV names now bind the same graph-managed texture. Resource read/write declarations plus region order retain queue synchronization; no extra CPU readiness timeline, history texture, traversal replay, root seed, world→GPU route or main-graph per-frame rebuild was added.

`vsm_page_hiz` and its reduction still dispatch only the actual dirty page prefix through the existing indirect arguments. The supported packed eight-by-eight tile/layer addressing and mip storage remain coherent with their C# descriptions. Unaffected cached tiles are not rebuilt or copied. This is a source-level removal of full-copy work, **not a measured millisecond or throughput saving**.

### History requires depth correspondence, not a new mapping

The existing physical-page metadata now separates HiZ correspondence from allocation:

- Allocation can carry prior HiZ only for the same world-page tag, matching completed whole-depth generation and unchanged light projection.
- New assignments, retags, masked depth, work failure and generation/projection changes do not acquire prior history from stale texture contents.
- Allocation clears the completed-HiZ generation while setting the existing metadata's main-stage prior-read flag. This flag survives intermediate/post work and is cleared at depth completion.
- Final HiZ precedes the completion marker. Only accepted whole-page depth receives next-frame HiZ correspondence; masked depth never does.

No new page-state buffer or CPU mirror was introduced. The page table owns sample completion; existing metadata owns HiZ correspondence. They are different facts, not two geometry-work status holders.

### Receiver coverage is not whole-page validity

A drawable mapping remains generation zero until all necessary main/post caster, material and surface work is accepted. The shared #134 draw resolver remains unchanged. `CompleteVirtualShadowPageDepth` consumes the same whole-frame geometry `WorkStatus` byte zero; rejection leaves sample and HiZ completion absent. **`Work.Complete` still says only that geometry work was not rejected. It says nothing about allocation, history or all receiver samples.**

Completed receiver-only depth uses a distinct page-table completion marker. The sampler validates the actual eight-by-eight receiver cell for each hard/blocker/filter tap, using the existing receiver masks. Whole-page depth can use the cached atlas fast path; masked depth cannot bypass per-tap coverage. Masks are reset in feedback **after shading**, not between depth completion and sampling. Masked pages are not retained as whole pages: a subsequent unchanged request promotes them by whole-page rendering before cache reuse; repeated caster changes continue to render and validate the current coverage.

Full/coarse demand remains full coverage even on an invalidated same-tag page. The coarse fallback request and all moving old/new/coarse caster invalidation remain in place. Newly exposed receiver cells are current-frame demand, not inferred from a previous partial page. Generation rollover avoids the reserved completion bit and requests a real cache reset before any generation is reused.

Production/user sampling automatically reflects `VirtualShadowReceiverMasks` through the existing feature resource provider. No vertex layout is assumed. Standalone whole-page fixtures can explicitly define `VSM_WHOLE_PAGE_SAMPLING`; partial completion is then rejected, never accepted as whole depth. The seven existing standalone D3D12 sampling shaders declare that mode for their already-whole-page inputs; their assertions and expected outputs are unchanged. This compatibility edit is not new internal-storage regression coverage.

### Missing or unfinished local depth is explicit

Public shader `TrySampleDirectionalVirtualShadowMap`, `TrySamplePointVirtualShadowMap` and `TrySampleSpotVirtualShadowMap` return validity separately from visibility. A completed coarse mip is tried before an invalid result. If any required blocker/filter tap has no completed covered depth at any available mip, the complete filter evaluation retries coarser; directional sampling can continue to the next clipmap. Missing taps neither fabricate blockers nor silently contribute zero visibility to an otherwise valid average.

If there is no completed footprint, the Try API returns **false with visibility 1**, not a valid empty sample. Default Standard point/spot callers explicitly handle this result without inventing occlusion. The existing float-returning material signatures remain compatibility factors; callers needing validity retain the Try result. Existing page marking retries missing/unfinished demand each frame, so later allocation/completion restores valid sampling. Actual no-mip/unfinished/recovery and independent point/spot GPU evidence are still required; this source policy is not a numerical-quality pass.

The inherited #133 corrections are preserved: each fallback tap returns its actual texel center for receiver-plane comparison, and only *completed* clear depth is treated as empty/lit. A clear but unfinished/masked-uncovered texel never proves that result.

### Invalidation and cancellation

- Existing invalidation still visits both old and new bounded caster footprints across all relevant mips, including coarse directional fallback and pages not requested this frame.
- Geometry/residency change revision and the unjittered primary-camera geometry-selection guard still advance depth generation. `ClusterMeshes` advances its manager revision for staged/published residency and eviction; no new streaming path was introduced.
- Material scalar edits now advance depth revision even when the binding/bin fast path is unchanged. User vertex/domain/opacity interpretation can depend on those values. Existing tracked texture notifications also invalidate depth for unchanged binding/in-place alpha or deformation changes. This deliberately conservative invalidation does not flush allocation or infer a fixed user layout. Shade-only edits may cost extra depth work; narrowing it needs supported shader dependency evidence, not removal of correctness invalidation.
- Existing bounded/unbounded external invalidation APIs remain necessary for externally modified GPU buffer or shader inputs that do not notify material/texture changes.
- No-submit cancellation rolls back CPU frame history without overwriting persistent HiZ. Partial submission without the final frame marker retains RG boundary states and forces reinitialization. A GPU geometry rejection does not stamp dirty depth or HiZ correspondence; Runtime's existing typed overflow checks precede requested output/final success. No late submitted pass is treated as proof of depth completeness.

### Coherent source/schema/content removal

`CopyPageHiZ` was removed from the shader settings block, its validation/resource realization, `VirtualShadowMap` construction/disposal and DefaultContent generation together with the copy kernel, pipeline wrapper, shader asset and import record. Content must be rebuilt from these sources; old shader-settings packets are not paired with the new binary. No atlas/work capacity, resolution, raster routes, material interpretation, quality defaults or independent reference gates were reduced or enlarged as a fix.

## Prepared public behavior — hardware blocked

`VsmCoverageImageTests.ExpandingReceiversDuringRepeatedCasterChangesPreservesFilteredShadows` uses only public startup, SceneScript and full SceneImage outputs. It expands receiver demand while moving a caster on successive frames, with normal bias and sixteen blocker/filter samples. First expanded and later repeatedly changed whole outputs are compared against a fresh final-state, mask-free scene; a no-VSM control must differ. Receivers and filter edges are retained. There are no test friends, private queues/storage assertions, exception-message assertions, sleeps or production fault switches.

This is **one authored behavior, not an executed red/green slice**. No list-tests command was run because Runtime theory discovery can create devices. Source-authored backend rows are not discovery/execution counts. Its baseline sensitivity has not been established. Do not claim TDD acceptance until the same behavior has an observed baseline red and implementation green, or clearly identify a different independently justified public regression. Existing #133/#134 image/failure cases also need rerunning on this combination.

## Actual CPU checks

Commands ran only in the owned tree, .NET SDK **10.0.112**:

```powershell
git submodule update --init external/Alimer.Bindings
dotnet run --project templates/DefaultProject/Game.Build/Game.Build.csproj -c Release -- --project "$PWD/templates/DefaultProject" --engine "$PWD"
dotnet build tests/SomeEngine.Runtime.Tests/SomeEngine.Runtime.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj -c Release -v minimal
dotnet build tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release -v minimal
pwsh -NoProfile -File tools/Check-TestRules.ps1 -ResultsDirectory artifacts/acceptance/ticket-135/rules
git diff --check
```

| Actual check | Result |
| --- | --- |
| Default source/content/shader build (two coherent cooks; final log retained) | 68 assets, exit 0; no device creation |
| Runtime Release build | exit 0, 0 warnings, 0 errors |
| Cluster Release build (includes Game.App) | exit 0, 0 warnings, 0 errors |
| D3D12 test-project Release build, including standalone sampling compatibility | exit 0, 0 warnings, 0 errors |
| Default test-rule checker | exit 0, 23 projects, 0 violations |
| Tests/list-tests/render/RT/GPU/performance | **not run**; discovered/executed/passed/failed/skipped all 0 for this checkpoint |
| Diff check | passed |

The initial Runtime build failed with missing Alimer.Bindings/MeshOptimizer (2 warnings, 8 errors), before the pinned own submodule `8d15f4e182f77df0c9af3aaf7ccd5dd8aeafc2a4` was initialized. This dependency failure is not a behavioral red. The first D3D12 test-project build also exposed the inherited sloped test calling nonexistent static `ExecuteSource` (0 warnings, 1 error). Its call now uses the already supplied `D3D12TestCompute(shaderCompiler)` fixture, with unchanged assertions; the subsequent build passed. A text edit initially rejected a nonunique `#endif` match; no replacement occurred, and the corrected unique edit succeeded. Neither event is acceptance evidence.

Exact final production commit, final build/check commands and source/content SHA-256 manifest are recorded with the retained [checkpoint artifacts](../../../artifacts/acceptance/ticket-135/README.md). No transient probe/script was created; generated shader packets/catalog are reproducible build products, not committed binaries or runtime validation.

## Ready / blockers / unchanged final gates

Ready for the coordinator's serialized GPU window **after reference-125 releases**. No authorization to discover devices, list device-creating theories, render or execute GPU tests was granted during this checkpoint. Reference-125's raw selected-frame/jitter-aligned capture and independent local-light branch has not been imported; parent coordinates it before final #135 validation. Merge the current integration ref into this branch before reporting and again if it advances before the hardware run.

Required next evidence: the matching public red/green; repeated dirty/filter receiver coverage; prior/current/reused-page and coarse/old/new caster outputs; cancellation/partial failure; alpha/material/streaming transitions; mapped unfinished/no-mip/allocation-pressure recovery; independent point/spot/range/cone and directional all-receiver reference; supported raster/backend/default beauty. No new runtime/source fixture substitutes for reference-125's owned independent truth.

Unchanged independent gates: mismatch **and mean error** ≤2%, false/lit ≤2%, missed/shadowed ≤2%, coverage ≥98%, ≥32 shadowed and ≥512 lit receivers, zero unexpected self-hit, all receivers/edges retained. Full beauty/RT and matched converged DefaultGame three-scene non-trace performance plus separate CPU/calibrated GPU diagnostics remain later gates. No speedup or equal-effective-quality assertion is made. #125/#127/#81 and full Standards/Spec remain open, as does #135 acceptance at this source-only checkpoint.

Retained citations only, not new private-source verification: UE commit `396c9f059903aed5fec78ecd3d437a40c6415368`, `VirtualShadowMapArray.cpp:4830–4920` (dirty HiZ main/post handoff), `VirtualShadowMapPhysicalPageManagement.usf:315–384` (receiver coverage/cache distinction), `VirtualShadowMapProjectionCommon.ush:413–426,533–605` (explicit local missing validity). No unretained full paths or live-source claims are added. See [#133](Vsm-Gpu-Work-133.md) and [#134](Vsm-Window-Work-134.md) for predecessor boundaries, not final combined acceptance.
