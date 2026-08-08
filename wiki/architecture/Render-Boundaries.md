# Render Boundaries

Rendering is split by responsibility. Assembly placement, ownership, and dependency direction
follow these product boundaries rather than backend or feature directory names.

## Render Domain

`SomeEngine.Render` is the backend-free render domain. It may contain render-facing ECS components, CPU-side render-world state, material and asset semantics, temporal settings, and renderer-independent data contracts.

`MaterialPass`, `PassEntry`, and `PassVersion` are accepted material/asset semantics when they stay detached from RenderGraph execution and GPU resources.

Material planning and shader identity sets are likewise domain/asset facts only. They select semantic intent and stable shader identities; they do not authorize PSO assembly, descriptor layouts, binding slots, or execution scheduling inside the Render domain.

It must not depend on a concrete graphics backend, RenderGraph execution, Editor renderer
integration, windowing, ImGui, native GPU handles, command recording, descriptor/root-signature
details, pipeline state, pass scheduling, pipeline caches, or present/swapchain code. A product
`Pipelines` folder belongs to execution, not to the Render domain.

## Graphics and Render Graph

`SomeEngine.Graphics` is the portable execution boundary. It owns the public immutable graphics
facts, lifecycle entities, command recorder, finished command lists, queues, and synchronization
coordinates. `SomeEngine.Graphics.Null` and `SomeEngine.Graphics.Direct3D12` are concrete
implementations of those owners; they do not publish a parallel backend object model.

Every native or logical lifecycle obligation has one owner object. Creation `Desc` values are
scoped complete inputs and are not retained as owner metadata. Resources, views, bindings,
pipelines, query pools, swapchains, work graphs, mappings, acquired images, bindless slots, command
recorders, and finished command lists all follow direct owner/borrow/transfer boundaries. Public
lifecycle handles plus `Destroy`, feature device interfaces, feature command contexts, backend
record mirrors, and detached `Info` packets are not part of the boundary.

`SomeEngine.RenderGraph` is one level above Graphics and depends only on portable owners. One
`RenderGraph` is one single-use invocation. It borrows imported owners, acquires internal transient
claims, declares passes with exact access relations, records through `PassCommandScope`, submits
finished command lists, and returns a `DevicePosition`. It does not create persistent physical
owners, export graph-created resources, expose raw recorder/resource bridges, or retain reusable
compiled topology.

Shader assets cross into execution through one exact projection to `ShaderArtifact` and
`ShaderSlot` facts. Pipelines and graph validation consume the same slot kind, stage visibility,
effect, qualifier, and shape facts. Assets, Graphics, and Render Graph do not maintain parallel
runtime shader-contract representations.

`SomeEngine.RenderGraph.Diagnostics` may explicitly materialize one immutable detached
`RenderGraphSnapshot`. It has no reverse dependency into core, does not retain a live graph, and
does not decorate command recording.

## RenderWorld ECS

`RenderWorld` is a dedicated ECS world and the renderer's only scene-extraction destination. MainWorld remains authoritative for game and authoring semantics; extraction reads it, synchronizes selected entity identities, and writes the minimum transformed render data needed for the frame being rendered. `Extract` is the only public extraction control. One call reads a coherent MainWorld snapshot and publishes one detached RenderWorld candidate atomically; a failed extraction leaves both the live RenderWorld and its renderer-owned state unchanged. There is no public extraction sequence or global scene version for pipelines to poll. Extraction is one-way. Render systems do not reach back into MainWorld while rendering, and a renderer result that must affect game logic returns through an explicit frame-stamped feedback channel.

RenderWorld is neither a complete clone of MainWorld nor a flattened instance snapshot. Its component model is render-specific: one render entity may combine shared extracted semantics such as transform, bounds, mesh, material bindings, visibility, and light data with optional components produced by Cluster, conventional raster, ray tracing, shadow, deformation, or debug preparation. A pipeline participates by querying the component combination it requires. Adding a pipeline adds components and systems; it does not add another scene-submission interface or widen one central instance record.

RenderWorld data has three distinct lifetimes:

- extracted components are immutable snapshots of MainWorld semantics for the frame being rendered and are replaced only by a later extraction;
- prepared components and caches are renderer-owned state that may survive extraction and may be mutated by the responsible render systems;
- frame-temporary entities, phase items, bins, and other transient state are rebuilt or removed at the frame cleanup boundary.

Mesh extraction attaches the marker `RenderInstance` and the exact render-facing components required for grouping. `RenderTransform` is itself the final sequential 48-byte shader ABI; `RenderPreviousTransform` preserves the previous extracted value without introducing another transform representation. There is no stable `RenderInstanceSlot`, entity-to-row owner table, free-list identity, persistent per-entity GPU row, hot-header object, or variable instance heap. Pipeline-specific residency, acceleration structures, bins, draw records, and other algorithm resources remain independent.

`RenderInstanceResources` owns one GPU-facing arena for batch-shared values, batch-local per-instance columns, and per-batch metadata. A row exists only for one live batch, is never an entity identity, and is never written back to ECS; one render entity may legitimately occupy rows in several independently composed batches. `RenderInstanceBatchMetadata` identifies the exact live batch, generation, row count, and immutable property contract but retains no duplicate CPU metadata array. Releasing the batch returns its rows to the arena, and foreign, stale, or layout-mismatched batches fail closed.

Property layout is composed explicitly before storage creation. A local `RenderInstancePropertyLayoutBuilder` combines strongly typed keys and exact encodings, then freezes deterministic metadata offsets and a contract id. An encoding states value size, alignment, stride, and metadata word count; it never assigns business meaning. Render contributes current/previous transform declarations, Cluster contributes only its geometry declarations, and a pipeline or material includes its own declarations without modifying a central manager or registry. Resolved properties are valid only for the exact frozen layout that produced them. Shaders read every linear property through the same generic `RenderInstanceLoad<T>` transport; the transport contains no type switch.

Pipeline systems build a batch through one `RenderInstanceWriteScope` inside the enclosing `RenderPrepareScope`. `TryBuildBatch` holds one topology-stable `RenderWorld.ExecuteReadSnapshot`, measures the exact row range, then visits the same snapshot's chunks and appends borrowed `QueryChunkView.Read<T>()` spans directly into their final mapped GPU columns. No entity list, instance DTO, property staging array, persistent component cache, or second mapped backing is created, and no borrowed span escapes the callback. A generic `IRenderInstanceBatchWriter` lets a downstream pipeline append new component types without changing Render or Cluster. Writer failure, batch-build failure, recursive entry, shutdown, and explicit release all return admission and rows; publication remains atomic at the enclosing `RenderPrepareScope` commit.

Frame coordination establishes one global read/prepare exclusion boundary per exact coordinator. Every open frame or observation holds read ownership, and every completion retained by a submitted read delays all later prepare scopes on that coordinator; timeline registration selects which resource owner publishes a sequence and advances its history, not which completions participate in the global mutation gate. Timelines, registration, and linear mutation leases remain assembly-internal synchronization details rather than public renderer APIs. An exact resource owner creates one timeline for each independently consumed mutation domain from one exact coordinator instance; sharing a graphics device does not make another coordinator an equivalent authority. Only a successfully completed submission publishes the next sequence of each timeline registered by that frame. Abandoned dirty prepares—including a prepare before the first submission—remain retry-required, so partially updated mapped state cannot leak into a later frame. Exact queue completions gate mutation and explicit resource shutdown without a device-wide idle. Raster-only work and observations do not advance Cluster history when they do not register its timelines, although their active or submitted reads can still delay the next global prepare. Extraction and unsubmitted frames do not advance temporal history. A pipeline resource owner reports its own diagnostics, and debug tooling does not create a second scene or behavior-changing control path.

## Cluster Renderer

`SomeEngine.Render.Cluster` is the backend-neutral runtime for immutable BVH topology, stable page identities, bounded page streaming, Cluster property contribution, and completion-gated residency publication. Device storage is accessed only through the portable Graphics boundary; no concrete backend API enters the Cluster model. Cluster owns no instance scene, slot, header, heap, property contract wrapper, material field registry, or persistent prepare system. `ClusterRenderFeature.InstanceLayout` declares current/previous transform, BVH root, material-slot offset, and bounds expansion; a caller composes any additional pipeline/material fields and supplies their generic writer. Cluster writes only its declared geometry values into the caller's batch and owns only algorithm resources such as BVH/page residency and mesh-cache state. It does not define shader permutations, handwritten binding slots, material-pass assembly, PSOs, or RenderGraph passes. Those contracts come only from the Graphics/RenderGraph boundaries and shader reflection. Diagnostics are observation data or separate visualization work. A diagnostic control must not silently change traversal, culling, residency, or LOD decisions.

The current Cluster runtime accepts only validated range-streamed `MeshPayloadSource` meshes; materialized payloads have no fallback adapter. Registration reads the BVH once directly into its final global-BVH allocation, validates its complete topology and authenticated page relation, and then changes domain state. Page bodies remain nonresident. The first demand for a page reads once directly into its final `PageHeap` allocation, authenticates those bytes, validates page layout and every cluster record in place, and only then makes the page publishable. Registration assigns stable CPU page IDs, records each page's exact global BVH leaves, and uploads every missing leaf with `ChildPointer = PageFaultMarker`; no additional GPU indirection resource is introduced. Publication patches those leaf pointers to the allocated byte offset. Staged offsets and roots are not observable as ready state until the execution owner has observed every completion dependency and explicitly confirms that fact to the CPU state machine.

A publication has one exclusive claim and one canonical write sequence: `PageHeap` bytes precede the GlobalBVH leaf-pointer fixups that make them reachable. Eviction first patches every affected leaf back to `PageFaultMarker`; the old heap range remains allocated until that write and every prior reader of the old pointer have completed. Physical heap or residency pressure selects an evictable least-recently-used page from explicit traversal-usage feedback, excluding the page currently being admitted, and defers the incoming load until leaf invalidation has published and reclaimed the old allocation. `NoCapacity` is terminal only when no eligible victim can satisfy the allocation. Failed submissions may return the claim only if no execution queue accepted it. Cluster does not poll `IDevice`, store queue fences, or invent a backend abstraction for that job. Fault ingestion preserves overflow information, exposes an explicit replay requirement, and is constrained by I/O-count and retained-byte budgets. Resident-page recency comes from explicit traversal usage feedback rather than pretending that page faults alone provide LRU information.

Registration and publication mutations follow one prepare/reserve/commit rule. Parsing, validation, ownership snapshots, capacity reservations, and immutable batch construction finish before domain state changes; the commit section does not allocate. Retrying a failed submission returns the same batch, while registrations and page work created after that batch remain pending for a later publication.

Every fault stream is bound to an explicit append-only `ClusterEpochId`. Fault and usage readbacks are accepted only with the assembly-local `ClusterReadbackTicket` cached by the exact `ClusterRenderResources` epoch; each binding exposes that ticket to readback scheduling, and raw bytes or leaf indices alone cannot be relabelled as a replacement epoch after restart. The fault stream's CPU inbox and downstream page-admission queue have independent hard bounds. Inbox overflow and admission backpressure both advance a replay generation, and acknowledgement names the observed generation so an older acknowledgement cannot erase newer loss. A corrupt or permanently impossible page is recorded as a structured terminal page failure instead of causing unbounded repeated I/O. Authenticating and versioning streamed page catalogs remains an asset-boundary responsibility; Cluster does not weaken page validation or emulate an obsolete payload to make such content load.

Public runtime diagnostics are immutable `ClusterRenderDiagnostics` aggregates over residency, instance, and mesh-cache state. Aggregate observation is serialized with aggregate preparation: a concurrent capture waits for the complete post-prepare image, while a recursive capture from inside the mutation fails instead of observing partial state or waiting on itself. Residency and mesh-cache diagnostics are derived from one `ClusterMeshesSnapshot`, so they cannot sample two versions of the same mesh epoch. Internal `ClusterMeshesSnapshot` and `PageStreamSnapshot` provide consistent samples of related counters, lifecycle, work ownership, publication state, and structured failure data without exposing live collections. `ManagerStateRevision` versions manager-owned state; the shared residency ledger is sampled alongside it and may advance independently. Page-stream shutdown is explicit `Active -> Disposing -> Disposed`: the stream retains its epoch lease while late loaders still own reservations, and the manager rejects disposal until that cleanup finishes. The manager's terminal snapshot is the logically empty epoch state; cumulative counters remain historical. Diagnostics do not expose raw exception objects or behavior-changing controls.

Cluster algorithm storage belongs to one public `ClusterRenderResources` lifetime owner. Construction injects the exact `RenderWorld` and `RenderInstanceResources`; device domain and frame-coordinator identity must match, and only one live Cluster owner may claim a RenderWorld. Cluster owns one exact base query for its fields. `TryBuildInstanceBatch` opens the shared instance write scope, visits that RenderWorld directly, writes current/previous transforms from ECS spans and produces BVH root/bounds values directly per row. Its generic overload accepts an exact composed shader layout, a caller-owned query, and an additional writer for arbitrary material or pipeline components. Cluster never caches those business values or learns their types. Cluster shutdown releases its query, RenderWorld claim, instance borrower, streaming, page heap, and BVH storage; it never confiscates caller-owned batches or retires Render-owned storage.

The assembly-local `ClusterRenderBinding` is a non-cacheable `ref struct` that combines Cluster-owned page heap/BVH/readback identity with the exact borrowed `RenderInstanceBatchBinding`. Its linear frame-use lease remains active until the binding is disposed, and frame use registers both Cluster residency and instance timelines. A foreign, released, or different-contract batch is rejected before frame registration, so no public split DTO can pair different epochs. Generated layout constants, `RenderInstanceLoad<T>`, and a real Slang reflection/compile test prove that C# `RenderTransform` and shader `RenderInstanceTransform` share the exact 48-byte field layout and that every Cluster consumer compiles for `glsl_460` SPIR-V and, on Windows x64, `sm_6_5` DXIL.

The asset transport/cooking boundary is the exact-schema `BinaryDocument<Mesh>` plus its one internal `MeshPayloadSource`; there is no asset dump, converter, resolver delegate, or alternate Cluster reader. ECS and RenderWorld carry the canonical strong `AssetHandle<Mesh>`. Cluster preparation accepts the owning `AssetLoader`, acquires one scoped read, and transfers that read to the append-only residency epoch while borrowing the Mesh-owned source; it does not retain a second source wrapper or mmap backing. The source type and retain operation are not public, so an external consumer cannot bypass the asset-read admission and keep an old mmap alive across replacement. Mesh reload therefore waits until pipeline shutdown releases the epoch, which disposes Cluster source use before releasing the asset read. Shader pipeline construction follows the same rule with `AssetHandle<Shader>` and pipeline-lifetime reads instead of retained raw `Shader` constructor values. Disappearing RenderWorld references are not authoritative unload events. Traversal follows the established leaf ABI: `PageFaultMarker` reports the global leaf node index, while any other leaf pointer is a `PageHeap` byte offset. CPU streaming maps the reported leaf through ClusterBvh's one registration-owned node-to-page relation; stable CPU page IDs are not written into the GPU leaf pointer. Diagnostics observe this state but do not define another residency representation.

A Cluster product `Pipelines` folder is likewise outside the Cluster domain/model.

## Boundary integrity

Observable behavior is defined by current owners, immutable facts, explicit algorithms, and
executable tests. Backend implementation details, historical control flow, fallback behavior,
incidental ordering, and source names do not become product contracts. Algorithms and data layouts
enter the product only when they follow from a recorded invariant and are exercised through the
current Graphics, Render Graph, Render, or Cluster boundary.

The current default renderer's reconstruction boundary, deform-cache contract, rejected copy
shapes, and remaining adopted-kernel provenance are recorded in
[[Cluster-Algorithm-Reconstruction]].

参见 [[Product-Boundary]]、[[Harness-Definition]]。
