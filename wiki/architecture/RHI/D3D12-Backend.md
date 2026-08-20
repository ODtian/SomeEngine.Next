# Direct3D 12 Backend

### RHI-D3D12-001 — Fixed native boundary

`D3D12Backend` is a sealed `IGraphicsBackend` implementation. Public abstract resources are converted
only after checking backend owner, expected private D3D12 derived type and, where relevant, exact
Device. Managed object identity is never proven with `Unsafe.As`.

The backend uses the pinned Agility SDK and Silk.NET native declarations. Public enum members and
operations have explicit D3D12 mapping branches; an unknown value is rejected rather than falling
through to a guessed native constant.

Slang target code, reflection and native D3D12 descriptions live only at the creation/encoding
boundary. Backend-private root slots, descriptor offsets and native leases do not escape as portable
state.
^rhi-d3d12-001

### RHI-D3D12-002 — Barrier encoding and cross-Queue handoff

The same public barrier contract lowers to Enhanced Barriers when supported and to the legacy
resource-barrier path otherwise. `BarrierPhase.Begin`/`End` map to native split transitions; D3D12
COMMON and queue-specific layouts are selected only inside this mapper.

Memory barriers become D3D12 global barriers, while Buffer/Texture/Aliasing barriers name the exact
native resources and ranges. Queue release/acquire operations preserve caller-authored synchronization
and do not create hidden fences or waits.
^rhi-d3d12-002

### RHI-D3D12-003 — Native root signatures and Pipelines

One root-signature builder consumes the exact linked S# program, selected reflected entries, static
samplers, Pipeline type and immutable Device capability facts. It walks live S# binding ranges and
creates only backend-private native placement. Validation never consumes this placement.

Serialized root signatures may be interned by byte equality. Each Pipeline still owns independent
Slang program/session/global-session references and the placement needed for its reflected owner.
Graphics, Compute and Mesh create native PSOs; DXR and Work Graphs create native state objects.

Synchronous and asynchronous entry points share the same creation cores and cache keys. Successful
asynchronous completion means the PSO/state object, roots, native names, Slang ownership and cache
admission are all complete.
^rhi-d3d12-003

### RHI-D3D12-004 — Direct native getters

`D3D12NativeAccess` is a typed Device capability for explicitly requested native integration. Getters
return borrowed native pointers whose validity is bounded by the documented public call or owning
object lifetime. They do not transfer COM ownership.

Command-list borrow is valid only while the Context remains in the required recording state and until
the next public Context/capability call. Every public command begins a new borrow epoch, including a
command later suppressed by state caching.

Native getters are not an unchecked parallel API. The direct backend still validates backend/Device
provenance before returning a pointer.
^rhi-d3d12-004

### RHI-D3D12-005 — Exclusive native Queue lock

Exclusive Queue-native access is represented by a stack-bound lock/lease sequence. Acquiring it
serializes against RHI submission and presentation on the same Queue. Dispose releases exactly that
sequence; copied or stale values cannot release another acquisition.

The lock does not create a second Queue, change completion numbering or transfer native Queue
ownership. Device teardown waits for the same synchronization authority before releasing Queue state.
^rhi-d3d12-005

### RHI-D3D12-006 — Native command encoding

Command methods first validate and resolve all public identities, then call concrete preparation
methods such as `PrepareCaptures`, `PrepareDescriptors`, `PrepareOrdinaryData`,
`PrepareBindingStorage`, `PrepareViewports`, `PrepareScissors` and
`PrepareRecordedRayTable`.

Only after every array, capture, descriptor range and temporary native argument has capacity does the
backend write the native command. State-shadow commit after the native call is no-throw. A warmed
recording path performs no managed allocation, reflection traversal, string lookup or LINQ.

Native command-list labels, events and object names feed PIX/debug/DRED diagnostics but never change
execution identity.
^rhi-d3d12-006

### RHI-D3D12-007 — Capability availability and native calls

Typed capabilities are constructed only when the selected adapter/runtime exposes every native fact
required by their public operations. Examples include:

- sparse-resource tier, format and tile information;
- sampler-feedback tier and descriptor/resolve support;
- residency interfaces and DXGI budgets;
- DXR tier, state-object and command-list interfaces;
- mesh/VRS tiers and limits;
- released Work Graph tier and properties;
- ExecuteIndirect argument support;
- linked-node masks and external-handle operations;
- presentation, calibrated timestamps and Pipeline creation support.

Native creation or command failure remains authoritative even after positive feature discovery.
Unreleased or unbound native features are not advertised by inventing a portable substitute.
^rhi-d3d12-007

### RHI-D3D12-008 — Production allocation, asynchronous creation and diagnostics

The private resource allocator uses 64 MiB Device-local blocks and 16 MiB upload/readback blocks,
partitioned by memory type, heap class and node masks. Eligible small resources use placed
suballocation; large or unsuitable resources fall back to committed allocation. Device-local block
growth checks DXGI budget pressure. Suballocations return only when their final `NativeLease`
releases, and each pool keeps at most one warm empty block.

Allocator construction and growth are failure-atomic: managed containers are reserved before native
Heap creation, native pointers remain in local cleanup scopes until ownership transfer, and failed
placed-resource construction returns both COM and suballocation state.

Each Device owns a bounded Pipeline creation queue with at most 256 requests and one to four worker
threads. Request packets own all span data, Slang global/session/program references and cache use.
Device teardown stops acceptance, faults unstarted work, joins workers and only then releases the
native Device.

`D3D12Diagnostics` is the single diagnostics surface. It exposes debug/DRED configuration, allocator
telemetry, asynchronous Pipeline telemetry, retained Device Lost data and per-Swapchain presentation
telemetry. DRED reports are immutable, preserve partial breadcrumb/page-fault success and mark every
bounded-chain truncation explicitly.
^rhi-d3d12-008
