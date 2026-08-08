# RHI Queue and Command Contract

### RHI-QUE-001 — Queue submission

The Queue gate is the one private mutex that prevents two CPU threads from issuing interleaved native
operations to the same Queue. Submit, sparse mapping updates and exclusive native Queue access all
use it. One Submit has
this observable native order: caller-provided QueueCompletion/ExternalTimeline waits; at most one
native command-list execute when the RecordedCommands span is nonempty; caller-provided
ExternalTimeline signals; then the private Queue-completion signal. An empty RecordedCommands span is
legal and still produces a real completion. Success consumes every
RecordedCommands atomically and advances every included `SwapchainImage` from Acquired to Submitted;
failure before native acceptance consumes neither command submission rights nor image state. A
Submitted image keeps only its one remaining Present right; it cannot be submitted a second time.
Concurrent submissions are serialized, while monotonic ordering of values signaled to the same
external timeline remains a caller precondition.

`QueueSubmitDesc` is a stack-only description over caller-owned spans: QueueCompletion waits,
ExternalTimeline `TimelinePoint` waits, RecordedCommands, SwapchainImages submitted by those commands,
and ExternalTimeline `TimelineSignal` outputs. The spans are consumed synchronously and may be stack
memory. Every wait precedes every command; the two wait spans have no interleaved-order meaning.
CommandLists may be empty; SwapchainImages must be exactly the current images actually referenced by
the submitted rendering work, without duplicates. Submit returns one QueueCompletion and no general
result wrapper.

Native acceptance begins with the first Queue operation that successfully enters the native Queue:
a successful Wait/Signal call or invocation of a nonempty command-list execute. All caller and state
precondition and state checks required for that Submit complete before this point; when present, the
Validation Layer also diagnoses contract violations before forwarding. Before native acceptance,
failure restores every input to its prior state. At or after it, Submit cannot roll back:
every command submission right is consumed and every included image has left Acquired. If a later
Wait/Signal or the private completion Signal fails, the Device becomes terminal, the inputs become
DeviceLost, and every internally owned retirement payload is retained until Device teardown because
no trustworthy completion watermark exists. Automatic mode also retains its captured public
resources, views, samplers, pipelines and bindings; Manual mode leaves those objects under the
caller's lifetime responsibility. A partially accepted Submit is never reported as an ordinary
status and its payload can never be submitted again.
^rhi-que-001

### RHI-QUE-002 — Completion value domain

Queue completion value 0 and `UINT64_MAX` are reserved. Values 1 through `UINT64_MAX-1` are legal;
allocation refuses before increment once the next counter has reached the final legal value. A
native completed value of `UINT64_MAX` is the D3D12 removal sentinel: observing it transitions the
Device to Lost and throws. It is never interpreted as "all targets complete."
`QueueCompletion` is an immutable borrowed value containing its Queue and legal Value; it has no
Dispose and the Queue's native timeline remains private. The default value (Value 0/no Queue) is
invalid: IsComplete, WaitCpu and use as another Submit's wait throw `InvalidOperationException`.
^rhi-que-002

### RHI-QUE-003 — CPU wait domain

Wait APIs follow .NET WaitHandle timeout semantics: zero polls, `Timeout.InfiniteTimeSpan` waits
without a deadline, and every other negative value or value above `Int32.MaxValue` milliseconds is
rejected before state or native access. Completed and Timeout are the only ordinary `WaitStatus`
values. Device loss is terminal and exceptional.
^rhi-que-003

### RHI-QUE-004 — CommandContext and RecordedCommands

Each Context is permanently bound to one Device, Queue, linked-adapter node and native command-list
type. Creation requires a nonzero initial slot count. Its reusable native slots never overlap: Begin
claims one completed slot or grows the retained pool to the observed in-flight high-water mark; it
never performs a hidden Queue wait. Growth completes before Reset and may fail exceptionally without
entering Recording. Stable operation at the high-water mark performs no managed allocation. Begin
captures the current descriptor generation.

`CommandRecordingDesc` capacities are initial reservations rather than hard caller-computed limits.
Their public names are `InitialResourceDescriptorCapacity`, `InitialSamplerDescriptorCapacity` and
`InitialCapturedResourceCapacity`; zero asks the Context to use its retained high-water mark/default.
The captured-resource reservation applies only to Automatic retention of referenced public objects; direct Manual
recording does not allocate or populate that arena, and a nonzero value has no execution effect there.
The optional Validation Layer reports a nonzero Manual-only unused reservation. Before an encode mutates the native command list
or state shadow, the Context reserves the complete descriptor space and, in Automatic mode, space for
strong references to every public object used by that operation. Resource/sampler ranges may grow
only within the native descriptor generation captured by Begin, while those Automatic references use
pooled chunks. A
new high-water mark may allocate/grow on that cold frame and is retained for reuse; stable recording
at the observed high-water mark allocates no managed objects or arrays. The RHI does not require
callers to pre-count every referenced public object merely to avoid an internal arena overflow.

If the captured native descriptor generation cannot satisfy another range without changing the heap
semantics of already recorded commands, the current encode throws `GraphicsException(OutOfDescriptors)` before
encoding that operation. Previously recorded commands and the state shadow remain intact, the Context
remains Recording, and Discard remains available. Managed/native allocation failure follows the normal
exception policy at the same pre-encode boundary. No overflow partially encodes a command, silently
switches descriptor generation, inserts a Queue wait or turns the initial reservation into an unsafe
array bound.

Every successful encoding call records the intrinsic native payload needed to execute that operation.
In Automatic mode it also strongly retains every resource, view, Heap, sampler, pipeline and binding
referenced by that call before returning; in Manual mode it retains none of those caller-visible
objects. End closes
the slot and transfers its native payload, mode-specific retained dependencies, native descriptor
generation and exact Queue compatibility into one `RecordedCommands`; Discard returns the recording
slot without creating an executable payload. End and Discard require Recording outside an open
rendering scope. Context disposal rejects new recording, abandons an active recording, and defers
native slot reuse until already-ended payloads retire. Device loss invalidates active recording and
all native payload operations without changing immutable provenance.

The command families form a strict inclusion hierarchy: Copy accepts common recording, barriers,
copy, labels, timestamp writes and query resolves; Compute adds compute pipelines/bindings, UAV
operations, predication and dispatch; Graphics adds rendering, graphics state, attachment operations,
graphics queries, draws and resolves that require a Direct list. Queue type must support the complete
family. BeginRendering is Graphics-only and non-nested; EndRendering requires its matching open
scope. Operations classified outside rendering cannot execute while that scope is open, and draw or
attachment operations classified inside rendering cannot execute without it. The executable
legality inventory is keyed by exact compiled signature, so a new overload receives no classification
by inheriting a method name.

`RecordedCommands` is single-submit state with the only normal progression
Executable → Submitting → Submitted → Completed. A pre-acceptance submit failure returns to
Executable; successful native acceptance never does. Dispose before acceptance produces Discarded;
Dispose after acceptance immediately ends the caller-visible `RecordedCommands` value and never waits or cancels GPU
work; the intrinsic submitted payload remains owned by Queue retirement until completion, together
with the referenced public objects only when Automatic retirement selected it.
Device loss produces DeviceLost and native payload access throws. Copies share one non-reusable
sequence and cannot duplicate submission rights. Dispose is no-throw/idempotent across copies. For
every initialized value, immutable Device/Queue provenance remains readable after submit/discard/
dispose; Status and native payload access remain sequence-checked. Default is invalid and its Dispose
is a no-op.

Bundle recording uses the same slot and sequence rules but only the bundle-safe command set; End
produces a reusable immutable `RecordedBundle`. Each execution retains the bundle's intrinsic native
payload and native descriptor generation in the containing command payload; Automatic mode also
retains the bundle's referenced public resources, while Manual mode leaves them under the caller's
lifetime obligation. Query and predication behavior is defined in
[[Queue-and-Commands#RHI-QUE-009 — Queries and predication]].
^rhi-que-004

### RHI-QUE-009 — Queries and predication

`QueryPool` is created with one immutable `QueryType`, QueueType, Count and resolved result stride;
Count zero is rejected. The core types are Timestamp, Occlusion, BinaryOcclusion,
PipelineStatistics and StreamOutputStatistics. Stream-output statistics also store stream index.
Acceleration-structure size/serialization output is emitted by the RayTracing capability rather than
pretending it is a native query heap. A pool reports its exact result layout as read-only Info so the
caller can size the resolve Buffer without reproducing a backend stride table.

Timestamp uses `WriteTimestamp` and has no Begin/End. Occlusion, pipeline-statistics and
stream-output-statistics use one BeginQuery/EndQuery pair on a compatible Graphics Context; a given
index cannot be active twice. `ResolveQueries` names one ended/written contiguous index range and one
destination BufferRange. It records one explicit resolve and does not wait, map or read the result.
The base backend always enforces pool Type, QueueType, index/range arithmetic, native stride and
destination byte bounds because those facts construct the native call. The optional Validation
Layer additionally tracks Begin/End/write/resolve history and produces the detailed misuse report.

Reusing an index while an earlier recording/submission may still execute is a caller synchronization
violation; no wait is inserted. Discard removes only validation history authored by that recording.
Automatic retirement keeps the QueryPool and resolve destination alive until the containing
completion; Manual retirement requires the caller to wait before reuse or Dispose.

`SetPredication(BufferRange, PredicationOperation)` accepts only EqualZero and NotEqualZero and
requires the reported alignment/usage. It is a future-state setter, so exact equal content may be
suppressed; the Buffer itself is retained by Automatic recording and by the Manual caller. Clearing
predication is one explicit null-buffer state. Predication does not change barriers or query state.
^rhi-que-009

### RHI-QUE-007 — Canonical initial synchronization facts

Resource creation establishes a public initial synchronization fact automatically; ordinary callers
do not repeat a backend-selected initial access/layout in every creation description. A newly created
ordinary Buffer begins with `Sync=None` and `Access=NoAccess` and has no Texture layout. A newly
created ordinary Texture whose contents are not initialized begins with `Sync=None`,
`Access=NoAccess`, `Layout=Undefined`, and undefined contents. Placed, reserved or aliasable resources
enter the same canonical state whenever a new public resource identity is activated over storage.
Upload/readback allocation forms have their fixed memory-type-specific initial access defined by
their creation contract; the caller does not select an arbitrary native state.

Created objects expose the immutable initial access, and Texture exposes its immutable initial
layout, so a manual barrier producer and Render Graph share the same first-use fact. An imported
resource is the exception because only the importer knows its external current access/layout and
Queue ownership; import requires those facts and the created wrapper reports them unchanged.

Successful Swapchain acquire returns the initial access/layout for that acquired image. It is
`Present` when prior presented contents are preserved, and may be `Undefined` when the backend and
acquire intent do not preserve contents, including a first-use/discard case. Present requires the
caller or Render Graph to have supplied an explicit transition to `Layout=Present` with no outstanding
resource access. The base RHI neither guesses the first transition nor inserts the final Present
transition. These immutable facts are RHI automation; barrier placement remains the responsibility of
Render Graph or a manual base-RHI caller.
^rhi-que-007

### RHI-QUE-005 — Explicit barrier ordinals

Every public barrier has a stable input ordinal and is encoded in order. The RHI may perform only a
necessary 1:N native expansion (for example discontiguous planes) and records the reason; it never
deduplicates, merges or reorders barriers. Cross-Queue ownership uses explicit release and acquire
operations plus a caller-authored timeline wait. Rendering, draws, dispatches, copies, clears,
resolves, query operations, acceleration-structure operations, bundle/indirect execution, submits,
presents and debug markers retain their own semantic ordinals and cannot be suppressed as redundant.
`BufferBarrier` synchronizes the complete Buffer. The portable contract exposes no byte range:
D3D12 buffer barriers cannot provide subrange synchronization, and callers must not infer independent
write ordering for disjoint regions of one Buffer. Texture barriers retain explicit subresource ranges.

`QueueRelease` names the resource/subresource range, its last source-Queue Sync/Access/Layout and the
destination QueueType. `QueueAcquire` names that same resource/range and source QueueType plus its
first destination Sync/Access/Layout. No public handoff object or intermediate layout is invented;
the backend derives each native side from the fields of that Release or Acquire. Between the source
release and destination acquire no Queue may access that range. The caller signals a timeline after
the source Submit and supplies the matching destination wait before the acquire. The base RHI neither
creates nor validates that cross-Queue order in the direct path; the optional Validation Layer tracks
the pair and diagnoses a missing/mismatched wait.
^rhi-que-005

### RHI-QUE-008 — Memory and aliasing barriers

The public barrier families are `MemoryBarrier`, `BufferBarrier`, `TextureBarrier`,
`AliasingBarrier`, `QueueRelease` and `QueueAcquire`. The public API does not copy the native D3D12
type name for its no-resource memory barrier.
`MemoryBarrier` has SyncBefore/SyncAfter and AccessBefore/AccessAfter but no resource or Texture
layout. It accepts the public access combinations whose semantics require memory visibility but no
resource identity or Texture layout transition. Resource-specific ordering or a Texture layout
change uses BufferBarrier or TextureBarrier instead.

`AliasingBarrier` is a stack-only description over two caller-owned spans. Each before entry names a
Buffer/Texture/AccelerationStructure identity or subresource range whose last access must finish;
each after entry names an identity/range activated over overlapping Heap storage. At least one side is
nonempty. The before entries become inactive, the after entries begin at `Access=NoAccess`, Textures
begin at `Layout=Undefined`, and their contents are undefined. The caller or Render Graph then emits
the ordinary BufferBarrier/TextureBarrier for the first real use. The RHI does not discover overlap,
choose the after resource, preserve aliased contents or insert the first-use barrier.

MemoryBarrier and AliasingBarrier are never suppressed, merged, deduplicated or reordered. Their
D3D12 encoding is defined only in
[[D3D12-Backend#RHI-D3D12-002 — Barrier encoding and cross-Queue handoff]].
^rhi-que-008

### RHI-QUE-006 — Exact state-suppression policy

Only normalized future-state setters may be skipped when content is equal and the native state shadow
is valid. The closed set is: pipeline/root artifact; persistent or transient parameter bindings;
vertex, index and stream-output buffers; viewports and scissors; blend constants; stencil reference;
depth bounds and bias; primitive topology and strip cut; predication; shading rate and shading-rate
image; and the corresponding bundle-safe setters. `SetWorkGraphProgram` may be skipped only when the
call has no initialization or reinitialization side effect. Native escape access invalidates every
shadow domain the borrowed native object may mutate; when that set cannot be bounded, it invalidates
the complete command state shadow.

Barrier, Begin/EndRendering, Draw/Dispatch, Copy/Resolve/Clear/Discard, Query, acceleration-structure
build/copy, ExecuteIndirect/Bundle, Submit, Present and marker/event calls are never suppressed.
Every product overload is classified in the implementation at its actual signature; a newly added
same-name overload does not inherit another overload's classification. Ordinary product tests cover
the closed set—there is no pre-implementation signature JSON catalog.

Normalized equality uses the public value type's .NET equality for enums, integers, records, floats
and spans compared element-by-element. Consequently `+0` and `-0` compare equal and all NaN values
compare equal under `Single.Equals`; no native bit-pattern distinction is introduced. Resource
objects compare by identity. Views compare by semantic descriptor type, Resource identity and
complete Description. CLR wrapper type, bindless decoration and DescriptorIndex are ignored when a
Bindless view is used through its ordinary base, so ordinary and Bindless forms with identical native
descriptor content compare equal. Samplers compare by complete Description. Pipeline and
persistent-binding state compare by their immutable compatibility
identity and materialized normalized content, not by wrapper reference. A native escape dirties every
shadow domain that the returned native object can mutate; when that set cannot be bounded, it dirties
the complete command state shadow.
^rhi-que-006
