# VSM HiZ lifetime and completed receiver depth — #135

Scope: [#135](https://github.com/ODtian/SomeEngine.Next/issues/135), parent [#132](https://github.com/ODtian/SomeEngine.Next/issues/132), implement-spec [#70](https://github.com/ODtian/SomeEngine.Next/issues/70). **Initial source checkpoint plus serialized GPU continuation. Concentrated public/local RT results are recorded below, but a native testhost abort blocks completion. No wrong-baseline numerical red, default three-scene RT/beauty or performance acceptance is claimed.**

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

## Prepared public behavior — initial CPU checkpoint

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

## Initial readiness / unchanged final gates

Ready for the coordinator's serialized GPU window **after reference-125 releases**. No authorization to discover devices, list device-creating theories, render or execute GPU tests was granted during this checkpoint. Reference-125's raw selected-frame/jitter-aligned capture and independent local-light branch has not been imported; parent coordinates it before final #135 validation. Merge the current integration ref into this branch before reporting and again if it advances before the hardware run.

Required next evidence: the matching public red/green; repeated dirty/filter receiver coverage; prior/current/reused-page and coarse/old/new caster outputs; cancellation/partial failure; alpha/material/streaming transitions; mapped unfinished/no-mip/allocation-pressure recovery; independent point/spot/range/cone and directional all-receiver reference; supported raster/backend/default beauty. No new runtime/source fixture substitutes for reference-125's owned independent truth.

Unchanged independent gates: mismatch **and mean error** ≤2%, false/lit ≤2%, missed/shadowed ≤2%, coverage ≥98%, ≥32 shadowed and ≥512 lit receivers, zero unexpected self-hit, all receivers/edges retained. Full beauty/RT and matched converged DefaultGame three-scene non-trace performance plus separate CPU/calibrated GPU diagnostics remain later gates. No speedup or equal-effective-quality assertion is made. #125/#127/#81 and full Standards/Spec remain open, as does #135 acceptance at this source-only checkpoint.

## Serialized GPU continuation after #125 integration

### Version, actual resource-interface failures and fixes

Resumed the clean `602bda9249d5439fb354ff7f1c9943d285ef7908` checkpoint. Integration `a0a17365e2055cde8dabd55543d5b6570409b4e5` was merged without conflicts as **`ff8aafc8e4a009d2e2f34d51872055d02b5c31f2`** before validation. Read the actual partial #125 merge/handoff. Capture/reference ownership was preserved; no denied root, sampling/resource/native-source/compiler test/Issue access was retried. The exclusive window allowed public GPU cases and source-compatible content building, not denied source access.

Two actual current-source receiver runs failed at **zero completed frames**, not at image accuracy:

1. `ff8aafc8`: six bounded resources expected, five supplied. A material receiver-mask declaration in the shared sampling include also appeared in administrative page pipelines. **`d94bcbf8571acacc2c686998af51f562da00db99`** explicitly selects page-definition/whole-only mode for all eight administrative producers. They never perform material receiver sampling; the default user material still gets coverage-aware sampling. No dummy per-pass binding or quality reduction was added.
2. `d94bcbf8`: twenty-four bounded resources expected, twenty-three supplied. The new target HiZ metadata had been bound for VSM target consumers but not the primary pipeline's neutral target interface. **`d497c04d`** binds the existing neutral page data in that same interface. It adds no new storage or mirror.

Both failed TRX/logs are retained; each has 2 rows, 1 executed/failed D3D12, 1 Vulkan Presentation skip. The same public behavior then passes. This is an actual source-interface failure→success, **not the required red on the regression-only pre-#135 production baseline**.

Coherent content/source checkpoint **`9333101b7920176b90d5f10c886505182a402086`** records regenerated import metadata after these source corrections. Content cook: **68 project assets**; payload manifest contains those 68 assets plus catalog (69 records). No production source changed after d497c04d. #125 raw/capture, independent rays and thresholds were unchanged.

### Observed baseline and final public results

A newly owned detached tree at **`d6085c7db41397db09c2e53cf143de5547fe88a3`** was confirmed clean, built and ran the exact new receiver-expansion test. **It passed**, 1 D3D12 pass / 1 Vulkan Presentation skip. It therefore establishes compatibility but **does not provide a baseline red or sensitivity proof**. No production mutation or receiver exclusion was used to manufacture one. The used detached tree was removed after the retained result was saved outside it.

| Actual coherent record | Result rows | Executed/passed | Failed | Capability skips |
| --- | ---: | ---: | ---: | ---: |
| receiver-expansion-coherent.trx | 2 | 1 | 0 | 1 |
| public-transitions.trx | 67 | 34 | 0 | 33 |
| headless-work-material.trx | 17 | 17 | 0 | 0 |
| Five completed isolated local RT hosts | 5 | 5 | 0 | 0 |
| **Completed scopes above** | **91** | **57** | **0** | **34** |

These are actual TRX row counts; no separate list-tests run is claimed. Runtime skips explicitly report Vulkan lacking **Presentation**. The headless batch passes both D3D12 and Vulkan; that does not establish Vulkan presentation. The public batch covers receiver masks/normal offsets in mesh, vertex and SW-capable routes, sparse old/new footprints, moving/coarse/rear/streamed casters, scroll/light changes, VSM×TAA, camera/geometry changes, cancellation and typed surface failure without incomplete output. The headless batch covers work-memory output, fully transparent user visibility, public output descriptions and ordinary/tessellated material replacement.

### Independent local reference on the actual #135 source

All five completed runs identify source `9333101b`; D3D12, 256² raw selected-frame output, captured submitted projection/transforms and unchanged full-receiver source-triangle reference. Each ran in a **fresh one-row testhost**; command filters, exit codes and 35.9–39.3 second walls are in `local-commands.json`.

| Completed case | Shadowed / lit | Mismatch AND mean | False / lit | Missed / shadowed |
| --- | ---: | ---: | ---: | ---: |
| point | 3839 / 38812 | 0.1078521% | 0.0566835% | 0.6251628% |
| spot | 3839 / 38812 | 0.11957516% | 0.07471916% | 0.57306592% |
| point-range | 2894 / 37846 | 0.26018654% | 0.1664641% | 1.4858328% |
| spot-cone | 3839 / 18938 | 0.2151293% | 0.13200971% | 0.6251628% |
| point recovery, new footprint | 4449 / 38149 | 0.12911404% | 0.086502923% | 0.49449313% |

All completed comparisons, including point recovery's old footprint, retain **100% coverage / zero self-hit**. Point-range's 3.6244% boundary mismatch remains included in its 0.2602% aggregate; no edge removal. Independent range/cone rejects remain 1915/20075. Point fixtures cover three cube faces, not all six. Recovery captures scene/submitted **63/62**, retains old frame31 comparison and configured 64-page pressure. **That setting alone does not prove fine miss, coarse fallback or no-resident-mip state.**

### Native blocker — not a sixth pass

The sixth host, `local-spot-recovery.trx`, aborted with **`0xC0000005`**, stack **Marshal.Release → ComObject.Finalize → GC.RunFinalizers**. Its run outcome is **Failed**, total/executed/result rows all **zero**. It is neither a failed assertion nor a capability skip, and is not included in the 57 completed-case passes. No completed spot recovery comparison exists for this source.

GPU execution stopped and the exclusive window was released after this abort. No repeat/native-source workaround was attempted. #125's earlier **two** equivalent zero-result native-aborted runs remain failures in their separate evidence, even though its later six isolated hosts passed. Those inherited passes do not turn this actual current-source abort into success or prove batch reliability. Root cause remains unknown; it has not been diagnosed as a VSM validity defect. The subsequent [native release investigation](Native-Release-Investigation-70.md) adds a public repeated-startup regression and a completed seven-case same-batch run, including spot recovery, on merged source `0755a124`. The native fault did not reproduce; no release fix is claimed, and this original zero-result abort remains a failed run rather than a pass/skip.

### Final CPU checks, provenance and remaining gates

After stopping GPU, the integration merge again returned Already up to date at a0a17365. Runtime, Cluster (including Game.App) and D3D12 test-project Release builds each exited 0 with **0 warnings/0 errors**. Default rule checker: **24 projects / 0 violations**. No denied standalone reference compiler or device test was run by these CPU checks.

All actual commands, baseline/source boundaries, TRX totals and native abort are retained under `artifacts/acceptance/ticket-135/gpu-window`. `content-payloads.csv`, `source-executable-hashes.csv`, actual raw/beauty/sidecar/capture provenance, `dotnet-info.txt`, `jit-environment.csv` and `test-totals.json` record this generation. Earlier CPU manifests remain earlier observations, not overwritten executable identities. The post-CPU recheck has **5 executable-byte differences** (Game.App exe/DLL, its Runtime DLL and the two test DLLs), despite matching sizes and unchanged production source; all **69 cooked payload/catalog records still match**, as does runtimeconfig. Historical executed-byte hashes remain in source-executable-hashes.csv; current bytes are separately recorded in post-cpu-byte-check.json. Do not claim current binary byte identity with the earlier GPU/test executables or rewrite the recorded hashes to manufacture it.

**Not completed:** regression-only baseline red; exact missing/unfinished/coarse/recovery-state proof; spot recovery/native host reliability; broader patterned alpha/custom deformation/all-six-face/backend coverage; partial-submission render-output failure recovery; owned Sponza/Bistro preparation and actual default **three-scene** raw directional RT/beauty; matched #81/#127 performance and full Standards/Spec. Import/default three-scene GPU work did not start after the target host abort. `Measure-Scenes` was first deferred for #81 CPU-build serialization, then remained unrun due to this blocker. No waits were relabeled GPU timings and no performance/quality gate was weakened. #135 is **not ready to close**.

Retained citations only, not new private-source verification: UE commit `396c9f059903aed5fec78ecd3d437a40c6415368`, `VirtualShadowMapArray.cpp:4830–4920` (dirty HiZ main/post handoff), `VirtualShadowMapPhysicalPageManagement.usf:315–384` (receiver coverage/cache distinction), `VirtualShadowMapProjectionCommon.ush:413–426,533–605` (explicit local missing validity). No unretained full paths or live-source claims are added. See [#133](Vsm-Gpu-Work-133.md) and [#134](Vsm-Window-Work-134.md) for predecessor boundaries, not final combined acceptance.
