# RHI Advanced Capabilities

This note defines the optional Device behaviors that are not part of the portable core. A
`DeviceCapability` is not a second backend and not an abstract unit of work. It is an immutable,
Device-scoped description of one named graphics feature and its limits. The actual operation is still
performed by the selected RHI receiver with the same Queue, CommandContext, resource and error rules
as the core API.

### RHI-CAP-001 — Discovery, absence, and errors

`TryGetCapability<TCapability>` is the only capability-discovery shape. It returns false and writes
null when that capability is wholly unavailable on a live Device. A default, foreign, disposed or
lost Device is not "unsupported" and follows the normal argument, state or DeviceLost exception
rules. The returned object is immutable, belongs to that Device and is borrowed for the Device's
lifetime; it has no Dispose and performs no virtual backend work itself.

The public capability types for the first D3D12 delivery are `SparseResources`, `SamplerFeedback`,
`Residency`, `RayTracing`, `MeshShaders`, `VariableRateShading`, `WorkGraphs`, `IndirectCommands`,
`CalibratedTimestamps`, `LinkedAdapters`, `ExternalResources`, `ExternalTimelines`,
`D3D12NativeAccess` and `D3D12Diagnostics`. Core Queues, bundles and query pools are not renamed as
capabilities merely because a Device reports their limits.

A capability exists when its named base operation is usable. Independently optional operations are
reported by a specifically named boolean, enum tier or numeric limit on that object. Invoking an
operation whose advertised prerequisite is absent throws `NotSupportedException` before any native
call. Invalid caller input uses the normal argument/state exception. A rare native failure throws
`GraphicsException`; Device removal also makes the Device terminal. Capability discovery is intended
for creation or algorithm selection, not for repeating a support test on every Draw or Dispatch.
There is no shader-program support preflight.
^rhi-cap-001

### RHI-CAP-002 — Sparse resources and Queue mapping updates

`SparseResources` supports reserved Buffer and Texture creation. A reserved resource is still an
ordinary Buffer or Texture identity. `GetSparseResourceInfo` returns the resolved tile shape, total
tile count, mip-tail/packed-mip placement and alignment for that existing resource; callers never
recompute those facts from format dimensions. Each `SparseMappingDesc` names one reserved resource
tile range and either one Heap tile range, one repeatedly reused Heap tile, or `Unmapped`. `Skip` is
not a public mapping kind; callers omit unchanged ranges. The input is a caller-owned span consumed
synchronously.

`UpdateSparseMappings(Queue, ReadOnlySpan<SparseMappingDesc>)` applies entries in span order and
returns a real `QueueCompletion`. It uses the same exclusive Queue gate as Submit and native Queue
locking. All range, overflow, Device, Queue-type and immutable sparse-limit checks needed to form the
native call finish before the first mapping call. The backend then issues one or more native mapping
calls in the supplied order and finally signals the Queue's private completion timeline; it never
inserts a Queue wait, command list, resource barrier or data upload. `CopySparseMappings` has the same
ordering and completion rule and copies only mapping state, not tile contents.

On D3D12 these operations use `ID3D12CommandQueue::UpdateTileMappings` and
`CopyTileMappings`; a future Vulkan backend uses sparse Queue binding. Invocation of the first native
mapping call is the acceptance boundary because D3D12 returns no HRESULT. A failure before that point
changes no mapping. Failure of the private completion signal after native acceptance makes the Device
terminal and the same mapping batch cannot be retried. In Automatic retirement, the returned
completion keeps every named reserved resource and Heap alive until it completes. In Manual
retirement, the caller keeps those objects and mapped Heap ranges alive through that completion.

Each accepted update also creates the resource's next immutable mapping generation. When Automatic
mode submits commands that reference a sparse resource, the submitted payload retains that current
mapping generation and every Heap it names until the returned QueueCompletion finishes. A later
remap/unmap changes only later Queue work; it cannot release a Heap still retained by earlier
submitted work. Manual mode does not retain those Heaps and requires the caller to keep every mapping
used by in-flight work alive. Mapping a resource concurrently from different Queues without explicit
ordering is a caller violation; the RHI does not serialize different Queues through a Device-wide
hidden wait.

Newly mapped or remapped memory does not acquire initialized contents or a new access/layout by
implication. The caller or Render Graph supplies the required aliasing, initialization and first-use
barriers. Unmapping does not wait for earlier uses; the caller orders it after their completions.
^rhi-cap-002

### RHI-CAP-003 — Sampler feedback

`SamplerFeedback` is present only when the Device supports feedback creation and resolve for at least
one format. It reports the native feedback tier, supported target formats, mip-region constraints and
feedback-map alignment. A `SamplerFeedbackTexture` is a Texture created for one immutable sampled
Texture and one feedback mode (`MinMip` or `MipRegionUsed`); it cannot later be paired with another
Texture. Its feedback UAV names both identities as required by the native descriptor.

`ClearSamplerFeedback` and `ResolveSamplerFeedback` are explicit CommandContext operations.
Resolution names its destination Buffer/Texture and mode; the RHI does not read back, decode on the
CPU, update sparse mappings, or submit another command list implicitly. Their Queue family,
alignment, format and resource-usage facts needed to build the native call remain base checks;
ordering hazards belong to the caller and the optional Validation Layer. Neither operation is a
future-state setter, so neither is suppressed. Automatic retirement keeps the sampled Texture,
feedback Texture, their views and the resolve destination alive through the containing
`QueueCompletion`; Manual retirement leaves that duty to the caller.
^rhi-cap-003

### RHI-CAP-004 — Explicit residency

`Residency` reports current local/non-local budget and usage as a read-only `ResidencyInfo` snapshot;
the snapshot is not a promise that a later request will fit. Only Heaps, committed resources,
descriptor heaps and query pools that the backend can make resident appear as `ResidencyResource`.
The RHI never performs MakeResident or Evict merely because a resource is bound or submitted.

`EnqueueMakeResident(Queue, ReadOnlySpan<ResidencyResource>)` explicitly schedules residency and
returns a `QueueCompletion`. On D3D12 the backend calls `ID3D12Device3::EnqueueMakeResident` with a
private residency fence, queues a wait for that fence on the supplied Queue, and then signals the
Queue's private completion timeline while holding the Queue gate. Work submitted later to that Queue
is therefore ordered after residency without exposing either private fence. Other Queues consume the
returned `QueueCompletion` as an explicit wait when they also need that order. The method neither
evicts other objects silently nor turns an over-budget request into an ordinary status. Range,
Device and support checks finish before the native request. Failure of EnqueueMakeResident throws
without a Queue wait; successful EnqueueMakeResident is the acceptance boundary. If the following
Queue Wait or private completion Signal fails, the Device becomes terminal, the request cannot be
retried and Automatic-mode objects remain retained until Device teardown.

`Evict(ReadOnlySpan<ResidencyResource>)` is a synchronous Device operation. The caller first waits for
every Queue use of those objects; Evict never inserts that wait. Automatic retirement keeps objects
named by `EnqueueMakeResident` alive through its completion. Manual retirement requires the caller to
do so. Residency state is independent from object lifetime: Dispose does not promise an immediate
Evict, and Evict does not Dispose an object.
^rhi-cap-004

### RHI-CAP-005 — Ray-tracing pipelines and acceleration structures

`RayTracing` reports pipeline ray tracing, inline ray query, indirect dispatch, acceleration-structure
update, compaction, serialization and state-object-addition support independently, together with
recursion, payload, attribute, geometry, instance and alignment limits. It is returned when at least
pipeline ray tracing or inline ray query is usable. A ray-tracing pipeline consumes only the linked
and specialized S#/Slang program. `RayTracingHitGroup` retains its state-object export identity, while
its closest-hit, any-hit and intersection members are S# `EntryPointReflection` values; an unused
member is null rather than a second shader-name string.

`GetAccelerationStructureBuildInfo` returns the result, build-scratch and update-scratch byte sizes
and alignments for the exact geometry/instance description. Build/update input is synchronously read
from caller-owned spans; the caller supplies the destination `AccelerationStructure`, scratch
BufferRange and every geometry/instance BufferRange. `BuildAccelerationStructure`,
`CopyAccelerationStructure` and `EmitAccelerationStructurePostBuildInfo` record exactly the requested
command and never allocate a hidden destination, add a barrier or become state-suppression candidates.
Compaction is an explicit size query followed by an explicit copy. Serialization/deserialization is
available only when its reported support is present and never silently substitutes a rebuild.

An `AccelerationStructure` is the storage identity. `AccelerationStructureSrv` is its ordinary
shader-visible descriptor, and `BindlessAccelerationStructureSrv` derives from that view and adds
only its stable descriptor index. Automatic retirement keeps every input, destination, scratch,
post-build destination, pipeline and descriptor referenced by an encoded operation until the
containing completion. Manual retirement requires the caller to keep exactly those objects and
ranges alive.
^rhi-cap-005

### RHI-CAP-006 — Ray-tracing shader tables and dispatch

`RayTracingShaderTable` is created for one `RayTracingPipeline`, fixed
ray-generation/miss/hit/callable record counts and declared record
capacities. A record selects an S# entry point or a hit-group export from that pipeline and supplies
parameter bindings matching its S# local parameter layout. Native shader identifiers remain scoped
to the pipeline/state object and are never accepted as caller-entered bytes.

`UpdateRayTracingShaderTable` is an explicit CommandContext operation over caller-owned record spans.
Before native command mutation, it reserves the required context upload/descriptor capacity and
materializes each record into context-owned storage. A successful call snapshots those records:
later caller-memory changes or table updates cannot alter an already ended `RecordedCommands`.
Updates retain their command order and are visible only to later dispatches in that order. No update
is generated when the caller does not call this operation.

`DispatchRays` names a table and direct dimensions. `DispatchRaysIndirect` additionally names an
argument BufferRange and requires the separately reported indirect-ray support. Both require a
Graphics or Compute command family that the Device reports as legal, never suppress, and never add
an implicit table update or resource barrier. Automatic retirement keeps the table, its pipeline,
the materialized record storage and every record binding alive through completion; Manual retirement
requires the caller to do so.
^rhi-cap-006

### RHI-CAP-007 — Mesh shaders and variable-rate shading

`MeshShaders` reports mesh/amplification availability and exact thread-group, payload, shared-memory
and output primitive/vertex limits. A mesh pipeline uses S# mesh and optional amplification entry
points; absence of an amplification stage is not represented by a fake shader. `DispatchMesh` and a
supported indirect mesh dispatch are explicit command operations and never fall back to a vertex
pipeline or CPU-generated draws.

`VariableRateShading` reports supported rates, combiners, per-primitive support, additional rates and
the shading-rate-image tile size. `SetShadingRate` may be called when per-draw rate is usable;
`SetShadingRateImage` additionally requires reported image support and an exactly compatible Texture. These
are future-state setters and may be suppressed only by the exact equality rule in
[[Queue-and-Commands#RHI-QUE-006 — Exact state-suppression policy]]. A Device with only the lower
tier reports the higher operation unavailable and that call throws `NotSupportedException`; the RHI
does not emulate the missing tier with extra draws or shaders.
^rhi-cap-007

### RHI-CAP-008 — Work Graphs

`WorkGraphs` reports the Work Graph tier, node/input/output limits and supported CPU/GPU input forms.
Pipeline creation obtains program, entry-point and node identity from S#/Slang. `ProgramName` remains
the state-object lookup identity. A `NodeIndex` is only a `uint` property inside
`WorkGraphEntryPointLayout` and means that reflected Work Graph node; it is not a reusable RHI-wide
"node" abstraction. The other permitted `NodeIndex` context is the linked-adapter capability in
RHI-CAP-011.

`GetWorkGraphMemoryRequirements` returns minimum, maximum and granularity for one created
`WorkGraphPipeline`. The caller allocates and supplies a backing BufferRange. `SetWorkGraphProgram`
also receives an explicit `WorkGraphInitialization` value. `Initialize` is mandatory the first time
that backing range is used, after unknown contents, or after use by a different Work Graph. It has
GPU-write semantics, is never suppressed and requires caller-authored synchronization. `Preserve`
asserts that the same graph last used the range and may be suppressed only when every normalized
future-state input is equal. The RHI never allocates backing memory or chooses initialization on the
caller's behalf.

When GPU input is used, the caller supplies the maximum input-record count before initialization and
it cannot exceed the pipeline's declared limit. `DispatchWorkGraph` consumes CPU record spans
synchronously or names explicit GPU BufferRanges; it never reads CPU records later. Work Graph
commands are legal only on the reported Graphics/Compute command families, not bundles. Automatic
retirement keeps the pipeline, backing range, input ranges and parameter bindings alive through the
containing completion; Manual retirement leaves that obligation to the caller.
^rhi-cap-008

### RHI-CAP-009 — Indirect commands

`IndirectCommands` reports each supported indirect argument type and alignment/stride/count limit.
`CreateIndirectCommandLayout` consumes a cold-path span of `IndirectArgumentDesc` whose `Type`
members are Draw, DrawIndexed, Dispatch, DispatchMesh, DispatchRays, WorkGraph or an explicitly
supported binding/state argument. It produces one immutable `IndirectCommandLayout` tied to the
compatible pipeline/root-layout signature; it never accepts a native command-signature pointer or
backend enum.

`ExecuteIndirect` names that layout, argument BufferRange, maximum command count and optional count
BufferRange. Required optional support is checked against the capability snapshot before recording.
The operation never changes unlisted state, inserts barriers, expands unsupported arguments on the
CPU or becomes suppressible. Automatic retirement keeps the layout, compatible pipeline artifacts
and both buffers alive through completion; Manual retirement requires the caller to do so.
^rhi-cap-009

### RHI-CAP-010 — Calibrated timestamps

`CalibratedTimestamps` exists only when the backend can sample a Queue timestamp and the platform CPU
clock as one calibration operation. `CalibrateTimestamps(Queue)` returns one read-only
`CalibratedTimestampInfo` containing CPU counter value/frequency and Queue counter value/frequency.
It takes the Queue gate long enough to prevent interleaving with exclusive native Queue access and
does not submit commands or allocate a query. The result is a snapshot, not a persistent clock
conversion guarantee; callers recalibrate at the cadence required by their profiler. Unsupported
Queues are rejected before the native call, and Device removal throws.
^rhi-cap-010

### RHI-CAP-011 — Linked adapters

`LinkedAdapters` exists only for a Device created from a native linked-adapter group. It reports the
node count and the creation/visibility masks supported for resources, heaps, Queues and pipelines.
Within this capability, `NodeIndex` means a D3D12 linked-adapter node and nowhere else. It is a
`uint` field in the owning descriptions, not a public object, handle or generic graph node. Resource,
pipeline, command-context and Queue construction store their node/mask provenance immutably; using
objects whose masks do not make them visible to the executing node is a caller contract violation.
The RHI never copies a resource between nodes or changes a visibility mask implicitly.
^rhi-cap-011

### RHI-CAP-012 — External resources and timelines

`ExternalResources` reports supported external handle types separately for Buffer, Texture and Heap
import/export. Import requires the external resource's current access, Texture layout and Queue
ownership because the RHI cannot infer them. Importing a native object states
`NativeObjectOwnership.Borrowed` or `Transferred`; a shared OS handle is opened synchronously and the
caller remains responsible for closing its input handle. Export is permitted only for an object
created/imported with the required shareable properties and returns an `ExternalHandle` that the
caller closes. Import/export never adds a copy, barrier or Queue wait.

The D3D12 native-access surface overloads `ImportBuffer`, `ImportTexture` and `ImportHeap` with the
corresponding native pointer plus `NativeObjectOwnership`; this is distinct from the backend-neutral
OS-handle overload. `Borrowed` performs no `AddRef` or `Release`, so the caller keeps the native object
alive through the wrapper's complete public and physical-use lifetime. For `Transferred`, ownership
passes after Device/capability/non-null-pointer/ownership-enum preconditions succeed. Every later
description, state, native-Device or registration failure releases that supplied reference, and a
successful wrapper releases it during terminal retirement. The native object must report the exact
native Device used by the target RHI Device. Neither form transfers ownership of an input OS handle.

`ExternalTimelines` owns `CreateExternalTimeline(initialValue)`, `ImportTimeline(handle)`, export and
user Queue wait/signal operations. Import opens the existing timeline value; it does not accept an
initial value or rebase the native fence/semaphore. Only `ExternalTimeline` can be exported,
user-signaled or returned by backend-native timeline access. Every Queue's private completion
timeline remains readable only through `QueueCompletion` operations and can never be exported,
returned as a native fence or signaled by the caller. Timeline values supplied by the caller must be
monotonic for each timeline; validation can diagnose a violation, but the direct path does not add a
per-signal history check.
^rhi-cap-012

The D3D12-specific ordering above follows the native contracts for
[tile mapping](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12commandqueue-updatetilemappings),
[asynchronous residency](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/nf-d3d12-id3d12device3-enqueuemakeresident)
and [Work Graph backing-memory initialization](https://microsoft.github.io/DirectX-Specs/d3d/WorkGraphs.html).
