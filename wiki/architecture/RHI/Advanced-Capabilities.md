# RHI Advanced Capabilities

### RHI-CAP-001 — Discovery, absence and errors

Optional functionality is represented by typed `DeviceCapability` objects returned from
`TryGetCapability<TCapability>`. Absence is an ordinary `false` result. Operations that require an
absent capability throw `NotSupportedException`; they do not emulate a different feature silently.

`DeviceFeatures` appears only in `DeviceDesc.RequiredFeatures` and `OptionalFeatures`. It requests
Device creation behavior and is not a post-creation capability database.

The current typed capability set includes Presentation, PipelineCreationSupport, SparseResources,
SamplerFeedback, Residency, RayTracing, MeshShaders, VariableRateShading, WorkGraphs,
IndirectCommands, CalibratedTimestamps, LinkedAdapters, ExternalResources and ExternalTimelines.
^rhi-cap-001

### RHI-CAP-002 — Sparse resources and Queue mapping updates

`SparseResources` reports tile size, supported sparse format sets and mapping limits. Sparse Buffer or
Texture creation produces ordinary Resource identities with immutable sparse metadata.

Mapping and mapping-copy updates are explicit Queue operations. Each batch validates resource/Heap
provenance, tile coordinates, packed-mip rules, bounds and overlaps before the native update.
Successful mapping creates a backend-private immutable mapping generation that retains exactly the
Heaps used by that generation. Recorded or submitted work retains the generation it captured until
completion. A later update never rewrites an older accepted generation.
^rhi-cap-002

### RHI-CAP-003 — Sampler feedback

`SamplerFeedback` reports tier, granularity, supported sampled/feedback formats and native alignment.
A `SamplerFeedbackTexture` is created for one sampled Texture and one feedback type. Its UAV names the
exact sampled and feedback resources required by the native descriptor.

Clear and resolve are explicit command operations. They do not decode feedback, change sparse
mappings, add barriers or submit hidden work. The command capture retains the sampled Texture,
feedback Texture, views and resolve destination through Queue completion.
^rhi-cap-003

### RHI-CAP-004 — Explicit residency

`Residency` reports point-in-time local/non-local budget and usage. `ResidencyResource` is a typed
reference to a native pageable object that the backend can actually make resident.

Make-resident and eviction are explicit. A successful asynchronous make-resident request returns a
completion that must be ordered by the caller before use. Evict is synchronous and assumes the caller
has completed all Queue use. Neither operation inserts a hidden wait or changes public object
lifetime. Device Lost after native acceptance is terminal.
^rhi-cap-004

### RHI-CAP-005 — Ray-tracing Pipelines and acceleration structures

`RayTracing` reports tier, Pipeline/inline support, limits, alignments and optional operations.
Acceleration structures are storage resources with explicit type and size. Build/update input names
every geometry/instance Buffer range, destination and scratch range. Build information is queried for
the exact description before recording.

Build, copy, compaction query, serialization and post-build information are explicit commands. No
operation allocates a hidden destination, inserts a barrier or substitutes a rebuild. Ray-tracing
Pipeline creation consumes the linked S# program, reflected exports/hit groups, exact limits and
static samplers. It is available synchronously and asynchronously through `IGraphicsBackend`.
^rhi-cap-005

### RHI-CAP-006 — Ray-tracing shader tables and dispatch

A `RayTracingShaderTable` belongs to one Ray Tracing Pipeline. Table capacity and record categories are
fixed at creation. An update supplies exact reflected entry/hit-group identities, local parameter
blocks and ordinary data. The backend validates record alignment/stride and materializes native shader
identifiers privately.

`DispatchRays` names a validated table, ray-generation record and direct dimensions. Indirect dispatch
uses explicit argument storage and capability support. Shader-table updates and dispatch do not add
barriers. The table, Pipeline, captured descriptor/native generations and record resources remain
physically retained through completion.
^rhi-cap-006

### RHI-CAP-007 — Mesh shaders and variable-rate shading

`MeshShaders` reports mesh/amplification limits and stage availability. Mesh Pipeline descriptions
contain the exact reflected mesh entry, optional amplification/pixel entries, fixed-function output
state and static samplers. Unsupported amplification or mesh features fail explicitly.

`VariableRateShading` reports tier, supported rates/combiners and shading-rate image tile size.
Per-command rate and image selection are explicit commands. A shading-rate image must satisfy the
reported format, dimension, usage, sample and tile rules. No ordinary raster state is reinterpreted as
VRS support.
^rhi-cap-007

### RHI-CAP-008 — Work Graphs

`WorkGraphs` reports the released tier and exact node, dispatch-grid, record and backing-memory limits
available from the runtime. Pipeline creation consumes the linked S# program and live node reflection;
it does not build a copied topology or semantic graph model.

The Pipeline exposes materialized entry-point information and backing-memory requirements. Dispatch
selects an explicit initialization mode and CPU/GPU input form. Backing storage, records, entry index,
counts and dimensions are checked against the capability and native ABI. Work Graph Pipeline creation
is available synchronously and asynchronously.
^rhi-cap-008

### RHI-CAP-009 — Indirect commands

`IndirectCommands` reports supported argument kinds and limits. `IndirectCommandLayoutDesc` is an
ordered native command-signature description with a fixed stride and, where root arguments require
it, an owning Pipeline.

Execution names the layout, argument/count Buffer ranges and maximum command count. The backend
validates alignment, Queue family, Pipeline type and every capability-dependent argument before
recording. D3D12 does not advertise a Work Graph indirect argument that its released native API does
not provide.
^rhi-cap-009

### RHI-CAP-010 — Calibrated timestamps

`CalibratedTimestamps` indicates that the backend can sample a CPU counter and Queue timestamp in one
calibration operation. `CalibratedTimestampInfo` reports both values and frequencies. It is a
measurement fact, not a Queue synchronization primitive.
^rhi-cap-010

### RHI-CAP-011 — Linked adapters

`LinkedAdapters` reports node count and masks for resource creation, resource visibility, Queues and
Pipelines. Device creation selects an enabled-node mask. Every node-specific Queue, resource,
descriptor and Pipeline operation validates that mask and does not infer cross-node visibility.

Single-node Devices use the same API with one enabled bit. Multi-node behavior requires real hardware
evidence; metadata tests alone do not certify linked-adapter execution.
^rhi-cap-011

### RHI-CAP-012 — External resources and timelines

`ExternalResources` and `ExternalTimelines` report exact handle types and import/export support.
Imported Buffers, Textures, Heaps and timelines remain ordinary RHI identities with validated native
description and initial state.

Borrowed import leaves the input native/OS ownership with the caller; transferred import consumes the
specified native ownership exactly once. Export returns a caller-owned `ExternalHandle`. Queue waits
and signals for external timelines are explicit members of `QueueSubmitDesc`; no hidden interop
synchronization is inserted.
^rhi-cap-012
