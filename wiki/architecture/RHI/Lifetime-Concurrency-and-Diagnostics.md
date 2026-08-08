# RHI Lifetime, Concurrency, and Diagnostics

### RHI-LIFE-001 — One Dispose contract

Every public RHI type that implements `IDisposable` or the C# disposable pattern has the same
caller-visible meaning: the first Dispose atomically ends that public lifetime, is terminal, does not
commit user work and does not throw an ordinary cleanup exception. Repeated Dispose is a no-op. It is
safe for multiple Dispose calls on the same object, and for parent and child Dispose calls to race;
normal behavior racing with Dispose remains externally synchronized.

Dispose never has a recoverable Failed/Rejected state and never depends on the caller retrying it
after fixing a lifetime error. The optional Validation Layer may report live descendants, leaked
objects or an invalid disposal order, but that report cannot interrupt cleanup. A parent proceeds to
end its lifetime subtree. Objects do not reopen and a stale identity never becomes valid for a
replacement Device or reused native slot.

After Dispose, only immutable managed metadata that does not touch a native object remains readable:
creation description, `Type`, provenance and diagnostic label/status where the owning type exposes
them. Native access and all behavior operations are invalid. The API does not add a universal
`IsDisposed` property. RHI objects do not rely on finalizers or GC timing for native cleanup.

Dispose releases or abandons work; it never performs an implicit End, Submit, Present, descriptor
publication, timeline signal, pipeline-cache persistence or other semantic commit. A type that cannot
portably and without failure release the claim it represents must not implement Dispose merely to fit
`using` syntax.
^rhi-life-001

### RHI-LIFE-002 — Parent disposal and cascading teardown

Every created object has the concrete parent named below. The caller may Dispose a returned object
early, while that parent remains able to destroy any native child storage still pending retirement.
Disposing a parent atomically prevents creation of new children and cascades logical disposal through
all remaining children in the
native API's legal order. Later Dispose calls on those child wrappers are no-ops. This makes `Graphics`, Device,
Surface, Heap, Resource and Swapchain follow one rule rather than giving the graphics receiver a
special retry or hidden shared-ownership convention.

The backend runtime destroys its Devices and backend-created Surfaces before destroying itself.
Device destroys all of its Queues, command contexts, resources, views,
samplers, pipelines, query pools, descriptor tables, persistent parameter bindings, pipeline caches
and external timelines. Disposing either the Device or Surface invalidates a Swapchain. Disposing a
Heap invalidates resources placed or aliased in it. Disposing a Buffer, Texture or
AccelerationStructure invalidates its Views. Disposing a Swapchain invalidates its image wrappers
and acquisition sequences. The backend maintains the minimum
intrusive lifetime registry needed for this cold-path cascade; it does not add public AddRef/Release,
ownership annotations, finalizers or per-hot-call reference tracking.

A Binding, Material or DescriptorTable slot does not own the resource/view/sampler it names.
Disposing either one does not Dispose the other. A new encode may
not use a disposed dependency. Both retirement modes retain the intrinsic native execution payload
that the RHI itself owns. Only Automatic mode additionally keeps alive the public resources, views,
samplers, pipelines and bindings referenced by recorded commands, published descriptor generations
and accepted Queue submissions. Manual mode leaves those objects under caller control. Neither mode
makes a retained payload the public owner of a disposed wrapper.

`DeviceResource.Device` therefore continues to mean association/device scope rather than a public
ownership annotation. Backend-owned Queues, Adapter/capability snapshots, completion values and other
borrowed values expose no caller Dispose. Native imports retain the explicit `NativeObjectOwnership`
distinction: Transferred native objects are released by the RHI; Borrowed native objects are never
released by it, because doing so would double-destroy external ownership. Their RHI wrappers still
obey the same terminal, idempotent Dispose contract.
^rhi-life-002

### RHI-LIFE-003 — Logical disposal and physical retirement

`RetirementType` is a required immutable Device-creation choice with exactly `Manual` and
`Automatic` values. `DeviceDesc` supplies it and the created Device exposes the selected value.
Every Queue, Context, descriptor generation and device-associated object follows that one selection;
there is no per-object override, live toggle or mixed-mode submission. Changing it requires quiescing
and creating a new Device. Context creation selects the corresponding recording storage so a Manual
Context has no public-dependency capture arena or capture work.

Public lifetime and native payload lifetime are distinct. The public progression is only Active to
Disposed. The internal payload may progress Live to Retiring to Released. Dispose always completes
the public transition immediately; an outstanding recording, descriptor generation or submitted
Queue operation may delay only physical release. A retiring payload never makes its disposed public
wrapper usable again.

Both modes retain the intrinsic execution payload required to finish and retire accepted work:
native command allocator/list slots, the captured native descriptor-heap generation, Queue
submission/completion state and any backend-owned payload that cannot be reconstructed after
submission. This is execution state, not per-use ownership of caller-visible resources.

Manual mode performs no automatic strong capture of public resource, view, sampler, pipeline or
binding dependencies. The caller waits for every relevant Queue completion and keeps those objects,
their native storage and descriptor targets alive before disposal or identity/storage reuse. A
Manual-mode Dispose is therefore the caller's assertion that this obligation has been satisfied.
The optional Validation Layer may track uses to diagnose an early Dispose or reuse, but that tracking
does not become shipping ownership.

Automatic mode additionally strongly retains every public resource, view, sampler, pipeline and
binding referenced by each recording, submitted payload and descriptor generation, then releases
those references only after every recorded Queue completion has finished. Collection is available
through `CollectCompleted(Device)` and may also be
driven by per-Queue asynchronous completion signals; neither mode creates per-object worker threads.
Physical descriptor ranges, native addresses, Heap ranges and logical indices are never reused before
their applicable Automatic retention owners retire or, in Manual mode, before the caller's valid
terminal Dispose certifies completion.

A Device is an execution-domain owner. Its Dispose closes creation and submission, invalidates active
recording/mapping/native-lock/acquisition scopes, discards unsubmitted payloads, cascades its lifetime
subtree and establishes the Queue quiescence required for native Device destruction. This final drain
may block because it is cold execution-domain teardown; routine resource Dispose never waits for GPU
completion in Automatic mode. After Device loss, teardown skips completion waits that can no longer
be trusted but still destroys native children before the native Device. A teardown failure is retained
as diagnostic information and does not escape Dispose.

The selected generic receiver or direct interface top-level refers to the same sealed backend-runtime
identity and the same single Dispose gate, but startup creates only one ownership root. Disposing that
root closes the runtime once; a construction-time backend reference confers no second disposal right.
Root teardown closes Devices and submissions, destroys Swapchains and all device-associated
descendants, destroys Devices, then Surfaces, and finally the backend runtime. Runtime backend
switching performs the same ordering before creating the newly selected backend.
^rhi-life-003

### RHI-LIFE-004 — Diagnostic delivery

The RHI has no public diagnostic-store identity. A failed Slang operation supplies its diagnostic
text directly to the creation exception; a failed native operation records the native code and
available backend message in `GraphicsException`; the optional Validation Layer reports text
synchronously to its configured sink. Diagnostics do not create a second success/failure transport,
and successful direct-backend hot operations never allocate or retain diagnostic records. Device-loss
diagnostics remain available through the Device's backend diagnostic capability until Device
disposal.
^rhi-life-004

### RHI-LIFE-005 — External timelines

Queue completion timelines are private retirement authority: they are never exported, exposed by
native access or user-signaled. `CreateExternalTimeline` creates a shareable timeline with an initial
value. `ImportTimeline` opens an existing shared timeline and neither initializes nor rebases it.
Only `ExternalTimeline` may appear in user signal, export and native-fence access operations.
^rhi-life-005

### RHI-LIFE-006 — Mapping lifetime

`BufferRange.Whole` is an input sentinel and is resolved at the call boundary to the Buffer's exact
`(Offset: 0, Size: Buffer.Size)` range. `MappedBuffer.Range` always returns that resolved absolute
range, never the sentinel. A single `Span<byte>` has an `Int32` length, so a resolved mapping larger
than `Int32.MaxValue` is rejected with `ArgumentOutOfRangeException` before native Map. Larger Buffers
remain valid and are accessed through sequential windows. Zero-length Map is rejected; zero-length
Flush or Invalidate is a no-op.

Flush and Invalidate consume absolute Buffer ranges wholly contained in `MappedBuffer.Range`; overflow
or escape is rejected before native access. A D3D12 Map returns the subresource base pointer rather
than a pointer adjusted to the requested read range, so the backend adds the resolved Buffer offset
before constructing the Span.

`MappedBuffer` is a stack-only sequence-checked mapping over that Span. Bytes, Flush and Invalidate
validate the captured mapping sequence. The first Dispose collectively ends the mapping once and is
no-throw/idempotent for every copy; it does not implicitly submit a copy or flush a range that the Map
mode leaves explicit. After disposal every previously obtained Span is contractually invalid, but the
runtime cannot revoke a raw Span value that caller code copied. Reusing a native mapping slot therefore
uses a new, non-wrapping sequence and never makes an old mapping active again.
The default MappedBuffer has no active sequence: Bytes/Range/Flush/Invalidate throw
`InvalidOperationException` and Dispose is a no-op.
Only one public mapping of a Buffer may be active at once, even for disjoint ranges; this is the
portable floor shared with backends that cannot nest mapping of one allocation. Map is externally
synchronized per Buffer. A second active Map cannot be represented without exposing two writable
Spans over one public mapping state, so the base RHI rejects it with InvalidOperationException even
without the Validation Layer. The layer may additionally diagnose the owning thread and original
mapping site.
^rhi-life-006

### RHI-LIFE-007 — Concurrency declarations

Concurrency uses ordinary .NET wording rather than custom public contract attributes. Each public
type's XML states its default as either thread-safe or externally synchronized; a member documents an
exception only when it differs from that type default. Queue submission methods, descriptor
publication and explicitly shared Device queries provide their documented synchronization. Command
contexts, mappings, swapchain acquisitions and Manual-retirement object use are externally
synchronized unless their specific contract says otherwise.

The Validation Layer records and reports violations of externally synchronized use. The direct
backend does not read thread IDs or maintain validation-only ownership state. XML also states the
call-site ownership facts that signatures cannot show: borrowed versus caller-disposed objects,
transfer points, post-Dispose availability, Device/Queue/context compatibility, expected Status
branches, exceptional failure and synchronous Span consumption. It links the relevant Wiki contract
instead of copying a state machine.

Concurrent Dispose calls are the one universal exception to an externally synchronized type default:
they are safe and collectively perform one logical release. Dispose racing with normal use is not
made safe by a hidden hot-path ownership check. Parent cascade and child Dispose synchronize only on
the cold lifetime registry and per-object terminal flag.
^rhi-life-007
