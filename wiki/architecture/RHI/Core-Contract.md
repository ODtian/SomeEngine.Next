# RHI Core Contract

### RHI-CORE-001 — Portable execution boundary

SomeEngine RHI is a backend-neutral execution API with explicit resources, views, Queues,
synchronization, descriptors, Pipelines, presentation and typed modern-GPU capabilities. Direct3D 12
is the first production backend. The strict conformance backend is an executable proof that the core
contract is not defined by D3D12 object shapes.

Backend-specific native access is opt-in and typed. Portable objects never expose D3D12 private
derived types as their public identity.
^rhi-core-001

### RHI-CORE-002 — One public product receiver

`IGraphicsBackend` is the only product calling surface. The selected backend object owns its native
runtime. `ValidationLayer` is an optional ordinary wrapper that takes ownership of that backend and
implements the same interface.

Resource, view and Pipeline objects are public abstract identities with backend-private sealed
implementations. Backend methods check backend ownership, concrete private type and, where required,
Device provenance before using them. Managed object provenance is never established with
`Unsafe.As`.

Generic dispatch adapters are permitted only inside the benchmark project so concrete and interface
receiver cost can be measured without becoming product concepts.
^rhi-core-002

### RHI-CORE-003 — RHI and Render Graph automation boundary

The RHI executes explicit work. It does not infer render passes, synthesize barriers, insert Queue
waits, compile shaders on first use, or own frame scheduling policy.

Render Graph may derive resource lifetimes, aliases, barriers and Queue dependencies. Its compiled
result must still call ordinary RHI creation, barrier, command and submission operations. A direct
caller can express the same execution without constructing a Render Graph.
^rhi-core-003

### RHI-CORE-004 — Slang/S# is the sole shader authority

A linked and fully specialized S# `IComponentType`, its live reflection objects and its emitted target
or entry-point code are the complete shader input to Pipeline creation.

D3D12 reads those facts on the Pipeline cold path and creates backend-private root placement.
Validation independently reads the same raw S# facts when validating caller packets. Neither side
consumes a normalized RHI shader tree, copied register map, shader package, cursor hierarchy or
backend placement produced by the other side.

When a native Slang fact is missing from S#, the binding is extended one-for-one. The RHI does not
reconstruct that fact from names, DXIL parsing or fallback identities.
^rhi-core-004

### RHI-CORE-005 — Error transport

Caller-contract errors use the standard argument and object-lifetime exceptions. A requested
capability that is not present uses `NotSupportedException`. Native or GPU operation failures use
`GraphicsException` with a stable `GraphicsError` and the native code when available.

Expected control-flow outcomes use typed statuses: CPU waits may time out; Acquire, Present and
Reconfigure have their own status domains. Device removal is never converted into an ordinary
presentation or timeout status. The first retained Device Lost exception remains the terminal Device
fact.
^rhi-core-005

### RHI-CORE-006 — Allocation and commit boundary

Every operation follows this order:

```text
validate
-> resolve backend objects
-> checked arithmetic
-> reserve managed/native capacity
-> build native arguments
-> issue native operation
-> commit no-throw state
```

After a native mutation or submission has been accepted, no required `Dictionary.Add`, `List.Add`,
`Array.Resize`, object capture or descriptor allocation may remain. Cold paths may allocate when the
operation requires owned data. A warmed command path must reuse its established high-water capacity
without managed allocation.
^rhi-core-006

### RHI-CORE-007 — Controlled vocabulary

A public type must correspond to a real API/GPU object, an independent state machine, independent
ownership, a type-level error prevention boundary, or behavior shared by genuinely different
backends.

The core vocabulary is intentionally small: Adapter, Device, Queue, Resource, View, Pipeline,
DescriptorTable, Parameter bindings, CommandContext, RecordedCommands, QueueCompletion, Surface,
Swapchain and DeviceCapability. Internal helpers are named for the concrete fact they store or the
specific preparation they perform; generic planning, artifact and transaction layers are not product
concepts.
^rhi-core-007

### RHI-CORE-008 — Stream-output Pipeline description

`StreamOutputState` is ordinary Graphics Pipeline state. Each `StreamOutputElement` names the exact
S# reflected output variable plus stream/component/output-slot data; explicit gaps carry no semantic
name. Buffer strides and the optional rasterized stream are caller state.

D3D12 lowers this directly to `D3D12_STREAM_OUTPUT_DESC`. Semantic storage is temporary and lives only
through native Pipeline creation. Stream output does not create another shader-interface model.
^rhi-core-008

### RHI-CORE-009 — Pipeline descriptions and immutable identity

Graphics, Compute, Mesh, Ray Tracing and Work Graph descriptions contain the linked S# program,
selected reflected entries and exact fixed-function or capability-specific state. Labels are
diagnostics and never affect compatibility or cache keys.

Synchronous and asynchronous creation have identical validation and native results. An asynchronous
method owns all span-backed description data and required Slang/cache lifetime before returning. A
successfully completed `Task<Pipeline>` is fully ready for binding; no deferred first-use compilation
is permitted.
^rhi-core-009

### RHI-CORE-010 — Adapter, Device and Queue creation

Adapter enumeration returns immutable identity, memory and driver facts. A Device request supplies an
Adapter, explicit Queue families/counts, required and optional feature requests, an enabled node mask
and a diagnostic label.

`DeviceFeatures` is only a creation request. Required requests must be satisfied or creation fails;
optional requests are enabled only when available. After creation, `Device.Capabilities` contains the
core limits/format table and `TryGetCapability<TCapability>` is the sole authority for optional
features.

Queues are Device-owned borrowed identities selected by type and index. Callers do not Dispose a
Queue; Device teardown invalidates submission and native access.
^rhi-core-010
