# RHI Lifetime, Concurrency and Diagnostics

### RHI-LIFE-001 — One Dispose contract

Every caller-disposed RHI identity has one terminal logical transition:

```text
Active -> Disposed
```

`DisposeGate` elects one releasing thread. Concurrent Dispose calls join that release and return only
after the same logical operation is complete. Reentrant Dispose on the releasing thread is ignored.
Release exceptions do not escape Dispose; the first teardown failure is retained by the owning
Device or backend diagnostics.

Immutable metadata explicitly documented by a type may remain readable after Dispose. Operations,
native access and mutable state do not.
^rhi-life-001

### RHI-LIFE-002 — Parent disposal and cascading teardown

The backend owns Devices and Surfaces. A Device owns its Queues, resources, views, Pipelines,
command objects, descriptor tables, query pools and capability-created children. A Surface tracks its
Swapchains. A Swapchain therefore participates in both Device and Surface teardown, but that is an
internal parent relation rather than a public lifetime graph.

Parent registries close registration under a gate, detach an intrusive work list in one pass and
perform child Dispose outside the gate. Teardown does not repeatedly scan a hash table and does not
allocate. Independent registry drain links are used only where one object belongs to two parent
registries.

Disposing a child first unregisters it. Disposing a parent first ends every child exactly once. A
child release failure is retained and prevents unsafe native-runtime unload rather than releasing
possibly referenced native state.
^rhi-life-002

### RHI-LIFE-003 — Logical disposal and physical native retention

Public wrapper lifetime and physical native lifetime are separate facts. `NativeLease` owns or
borrows one COM reference and may retain only dependencies required by the native object:

- a placed resource retains its Heap allocation;
- a view retains the resource whose descriptor it names;
- a Pipeline retains native root/state objects and its Slang program ownership;
- persistent bindings, indirect layouts and shader tables retain the Pipeline state they use;
- a command recording that binds persistent parameters retains the exact immutable published
  parameter-data generation, not the mutable public wrapper; the same generation is retained once per
  recording even when selected by several material runs;
- RecordedCommands retain captured native objects;
- sparse mapping generations retain mapped Heaps;
- swapchain image generations retain their back buffers.

When `Submit` is accepted, command and presentation dependencies remain retained until the Queue
completion retires them. Caller disposal can end a wrapper before that completion without invalidating
accepted GPU work. There is no public switch that lets a caller disable this correctness rule.

Native constructors are failure-atomic: a failed dependency retain or managed owner construction
returns every already-acquired COM reference and suballocation.
^rhi-life-003

### RHI-LIFE-004 — Device Lost and diagnostic delivery

A Device has the terminal status domain `Active`, `Lost`, `Disposed`. The first native removal result
creates one retained `GraphicsException(GraphicsError.DeviceLost)`. Later operations observe that
same terminal authority.

D3D12 diagnostics may attach a structured immutable DRED report containing independent breadcrumb
and page-fault query results, copied names/contexts, page-fault address, allocation candidates and
explicit truncation flags. Failure to capture one DRED branch does not erase the other branch or the
original Device Lost exception.

Release failures are diagnostics, not an excuse to continue using the object. If complete native
cleanup cannot be proven, the backend retains the affected runtime in process quarantine rather than
unloading it unsafely.
^rhi-life-004

### RHI-LIFE-005 — External timelines and handles

An `ExternalTimeline` is a caller-disposed Device resource backed by an importable/exportable native
timeline. `TimelinePoint` and `TimelineSignal` are pure values used explicitly in `QueueSubmitDesc`.
The RHI does not insert implicit external waits or signals.

`ExternalHandle` owns exactly the returned OS handle and closes it once. Import borrows the supplied
handle value synchronously unless the import API explicitly states transfer. Exported handles do not
extend the logical lifetime of the public resource object.
^rhi-life-005

### RHI-LIFE-006 — Mapping lifetime

`Map` returns a `MappedBuffer` value tied to one internal mapping sequence and an absolute Buffer
range. Copies of that value share the same terminal sequence. `Flush` and `Invalidate` accept only
absolute subranges contained by the mapped window. A zero-length range is valid only at a contained
boundary.

Dispose ends the shared sequence exactly once and performs the native Unmap contract. A stale copy
cannot operate after the sequence ends. The D3D12 backend rejects a second active mapping of the same
resource where its native mapping policy requires exclusivity.
^rhi-life-006

### RHI-LIFE-007 — Concurrency declarations

Public XML declares one default for every exported type:

- immutable values, Device metadata, capabilities and borrowed Queue identities are thread-safe;
- mutable caller-owned resources and recording objects are externally synchronized;
- immutable Pipeline state may be bound concurrently from independent recording contexts;
- `PersistentParameterBindings` may be bound concurrently while updates publish immutable
  generations; each binding operation observes and retains exactly one generation before native
  mutation;
- concurrent Dispose calls are always safe and join one release;
- Queue submission and completion queries are internally synchronized;
- normal operation racing with Dispose is invalid unless a type states a stronger rule.

A method-specific lock may serialize a native Queue, descriptor publisher, allocator or Pipeline
compiler without changing the public concurrency model. Diagnostic snapshots are immutable and may
be read concurrently.
^rhi-life-007
