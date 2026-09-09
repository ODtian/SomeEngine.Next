# Asset enhancement — implementation and verification

Date: 2026-09-08. Device: BOQI-THE-DAEMON.

Worktree: `F:\SomeEngine.Next-asset-enhancement`.
Branch: `codex/asset-enhancement`.
Base HEAD: `58dec043cc5c8e8483229ff4bd5cbd6026181bde`.
Changes remain in the working tree; this task did not commit, push, or merge them into another branch.

## Implemented boundary

`AssetId<T>` is a typed GUID value. `Asset<T>` is independent access to disk content and a concrete opened document version. `T` is an explicitly created domain object, not the decoded document root and not the loader's unique object for a GUID.

The loader owns bounded file reads and cache entries, not Texture/Mesh/Shader/Material instances or caller-supplied storage. Shared reads return independent closable accesses. Field capture and document duplication isolate mutable fields, nested data and ignored byte payloads. Canceling one shared-read caller does not cancel the others. Unversioned storage does not retain completed files as an indefinitely fresh cache entry. Opened assets report the verified document generation.

The runtime-object resident table, root-transfer load context, duplicate GUID/reference wrappers, object-read handles and descriptor layers were removed from the active asset path. `AssetFormat<T>` is generated/internal. Directory records are consolidated in `AssetEntry`; source import records and their I/O are consolidated in `SourceMeta`. Pack priority is handled by pack storage and changed-only writing by the pack builder.

Textures retain coarse-mip streaming, partial uploads, valid mip ranges, block-compressed layouts, cubes/arrays/volumes, multiple devices, canceled-upload rollback and submission-protected release. Procedural creation and update need no disk ID. `Texture.ReadDescription` and `Texture.ReadMipTiles` inspect files without constructing a texture. Meshes retain direct-to-target page/BVH reads and independent byte-source lifetime. Shaders support selected-backend creation and later backend reads. Concrete materials retain parent-file overrides, generated typed properties and actual texture/shader references; notification errors cannot dispose newly committed dependencies. Scene creation uses the existing World and extraction route and cleans up partial dependencies/entities on failure.

File writes use same-directory temporary files and atomic replacement. Already opened documents are copied with bounded buffers, their original catalog/compression/version preserved, and chunk verification before replacement. Failed or canceled writes leave an existing destination intact. Default assets and import records were regenerated.

The canonical type/responsibility and lifecycle document is `docs/AssetSystem.md`. `docs/BinarySerialization.md` and the architecture wiki now refer to the separated model.

## Build and code checks

Final command:

```powershell
dotnet build SomeEngine.slnx --no-restore -v:quiet -m:1
```

Exit 0, 0 warnings, 0 errors.

`git diff --check` exited 0. Git printed only its existing LF-to-CRLF normalization notices for three files, not whitespace errors.

The default-asset generator and RenderScenarios projects were also built successfully. `dotnet run --project tools/GenerateDefaultAssets/SomeEngine.GenerateDefaultAssets.csproj --no-build --no-restore -- --reimport` completed successfully and regenerated textures, materials, pipeline/configuration files, scene data and the manifest.

## Tests

The full solution test command was executed with TRX output. That run discovered two remaining failures: the unrelated-job closing-thread exemption in ECS, and an over-strict rejection of unversioned asset storage. Both were fixed; the complete affected project suites were rerun successfully. The following table uses each project's final confirmed result, not a claim that the first full-solution invocation had no failures. Repeated diagnostic runs are not added to the total.

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| SomeEngine.Assets.Tests | 148 | 0 | 0 |
| SomeEngine.Assets.Consumer.Tests | 20 | 0 | 0 |
| SomeEngine.Core.Tests | 57 | 0 | 0 |
| SomeEngine.Diagnostics.Tests | 20 | 0 | 0 |
| SomeEngine.ECS.Fuzz.Tests | 17 | 0 | 2 |
| SomeEngine.ECS.Serialization.Tests | 169 | 0 | 0 |
| SomeEngine.ECS.SourceGen.Tests | 39 | 0 | 0 |
| SomeEngine.ECS.Systems.Tests | 288 | 0 | 0 |
| SomeEngine.ECS.Tests | 534 | 0 | 0 |
| SomeEngine.Graphics.Direct3D12.Tests | 405 | 0 | 1 |
| SomeEngine.Graphics.Tests | 22 | 0 | 0 |
| SomeEngine.Graphics.Vulkan.Tests | 43 | 0 | 0 |
| SomeEngine.Job.Dots.Tests | 5 | 0 | 0 |
| SomeEngine.Job.Tests | 176 | 0 | 0 |
| SomeEngine.Render.Cluster.Tests | 118 | 0 | 0 |
| SomeEngine.Render.Tests | 83 | 0 | 0 |
| SomeEngine.RenderGraph.Tests | 76 | 0 | 0 |
| SomeEngine.Runtime.Tests | 19 | 0 | 0 |
| SomeEngine.Serialization.Tests | 140 | 0 | 0 |
| **Total, 19 projects** | **2379** | **0** | **3** |

The three skips are the explicitly configured environment fuzz campaign/trace tests (no input supplied) and the Intel-specific hardware test (Intel hardware unavailable). They are not disabled asset tests.

`WorldClosing_RejectsUnrelatedJobCallback` also passed eight consecutive isolated runs after the lifetime fix. Its strict expected exception was not relaxed. A job callback helped by the closing thread can no longer inherit that thread's direct teardown exemption.

Two earlier test callers were migrated without relaxing product guarantees: managed component data uses the durable-component API while reference-free world checkpoint tests retain entity patching coverage; a RenderGraph test waits only for the completion records actually returned by execution.

Important asset acceptance coverage includes independent opens and instances, field/input isolation, same-length/same-time file changes, loader shutdown, failed source cleanup, count/byte cache eviction, shared-read cancellation, bounded read concurrency, atomic verified copy, new downstream asset types without internal access, public texture metadata inspection, shader backend reads after external close, material overrides and post-commit observer failures.

The Render suite includes eleven D3D12 texture cases covering coarse mip, BC layout, no render-time asset I/O, canceled upload, independent devices, shape updates, procedural data, recorded-frame release, cube/cube-array/volume uploads, two-device procedural updates and device removal during an update.

## NativeAOT

```powershell
dotnet publish tools/SomeEngine.Assets.AotSmoke/SomeEngine.Assets.AotSmoke.csproj -r win-x64 -c Release -v:minimal -m:1
& tools/SomeEngine.Assets.AotSmoke/bin/Release/net10.0/win-x64/publish/SomeEngine.Assets.AotSmoke.exe
```

Both exited 0. Final native executable output:

```text
assets-nativeaot-smoke:ok schema=6C549EA74CAE2856 instances=2 independent-open=ok streaming-after-close=ok update=ok extension=ok
```

Publishing performs the RID-specific restore. A prior `--no-restore` attempt after a framework-only restore correctly reported missing `net10.0/win-x64` restore assets; the verified command above includes restore.

## Actual application rendering

The hardware application was executed, not merely compiled:

```powershell
& src/SomeEngine.Runtime/bin/Debug/net10.0/SomeEngine.Runtime.exe --frames 24 --hidden-window --hardware-raster --no-mesh-shaders --synchronous-frames --no-vsync --verify-frame-output
```

Exit 0. Confirmed output:

```text
sceneInstances=1024
Cluster residency: meshes=1/1, pages=6/6, missing=0, queued=0, active=0, failures=0.
Frame output: 1280x720, colors>=256, different=455801, bounds=(24,24)-(949,669), hash=A99E7E971267D7AA.
Cluster frame 24: candidates=6572/103 groups, phase1=0 SW+4148 HW, phase2=0 SW+0 HW, shadeBinTiles=454, shadeItems=48875.
RenderGraph structure: declaredPasses=128, livePasses=108, scheduledPasses=108, queues=3, analysisReused=15/24.
SomeEngine runtime completed 24 frame(s).
```

This command uses the hardware raster path without mesh shaders. It proves the regenerated default files, loader, domain creation, World extraction, geometry page loading, material/shader use and presentation connect through a real rendered frame. It is not a claim that every hardware feature permutation received a full-application run.

A three-frame WARP application attempt ended before geometry pages finished streaming and failed the final geometry-output predicate. A longer WARP application run was stopped because the 1,024-instance software-rendering workload was heavy; it is not counted as a passed 24-frame WARP run. The dedicated WARP texture tests passed, and the hardware application completed the unchanged final-output verification. Some new command requests were transiently rejected by the tool's safety-state service; confirmed results above were obtained from actual successful executions afterward.

## Scope limits

The refactor preserves existing chunk loading, partial texture upload and low-level sparse-resource capabilities. It does not claim a newly implemented automatic texture-feedback-to-sparse-mapping-and-GPU-eviction loop. CPU chunk budgets are not GPU texture residency budgets. No long-duration hardware soak or full application run for every backend/raster permutation was performed.
