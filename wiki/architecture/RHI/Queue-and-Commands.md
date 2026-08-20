# RHI Queue and Commands

### RHI-QUE-001 — Queue submission

`QueueSubmitDesc` explicitly names Queue-completion waits, external timeline waits, one or more
`RecordedCommands`, acquired `SwapchainImage` values and external timeline signals. Every item must
belong to the selected Queue's Device and satisfy its Queue-family contract.

Submission validates and reserves all managed/native retirement storage before native acceptance.
Before acceptance, failure restores every command and image to its prior state. After acceptance, the
submission owns all captured native dependencies until its returned `QueueCompletion` retires; a
post-acceptance failure is terminal Device loss rather than a retryable partial submit.

A `RecordedCommands` payload is single-submit. A submitted swapchain image can be presented only on
the Graphics Queue that owns its swapchain.
^rhi-que-001

### RHI-QUE-002 — Completion value domain

`QueueCompletion` is the exact pair of a borrowed Queue identity and a nonzero monotonically assigned
value from that Queue. Values from different Queues are not interchangeable even when their integers
match.

`IsComplete` and `WaitCpu` observe that Queue's private completion fence. `CollectCompleted` releases
retired submissions, descriptor generations, presentation generations and capability payloads whose
completion has passed. Completion collection never changes public command results.
^rhi-que-002

### RHI-QUE-003 — CPU wait domain

CPU waits accept a nonnegative `TimeSpan`, `Timeout.InfiniteTimeSpan`, or a value representable by the
native millisecond timeout. The result is `Completed` or `Timeout`; Device Lost remains an exception.
A zero timeout is a poll and does not insert a Queue wait.
^rhi-que-003

### RHI-QUE-004 — CommandContext, RecordedCommands and bundles

A `CommandContext` is created for one Queue type/index and may be an ordinary list or a bundle. Its
recording lifecycle is:

```text
idle -> Begin -> recording -> End/EndBundle or Discard -> reusable idle
```

`CommandRecordingDesc` supplies initial capacities for resource descriptors, sampler descriptors and
captured resources. These are preparation hints, not logical limits.

`End` returns a value-type `RecordedCommands` handle with a sequence-protected payload. Its states
cover executable, submitting, submitted, completed, discarded, Device Lost and caller-disposed
outcomes. Copies share the same terminal sequence. Disposing an unsubmitted handle discards its
native payload; disposing after acceptance does not release GPU dependencies early.

Bundles are caller-disposed Device children and may contain only the command inventory permitted by
the backend. `ExecuteBundle` captures the bundle and its native dependencies.
^rhi-que-004

### RHI-QUE-009 — Queries and predication

Query pools are typed by query kind and Queue family. Begin/end queries, timestamps, resolves and
Pipeline/stream-output statistics use explicit slots and destination Buffer ranges. Resolve checks
query count, destination stride, usage and bounds before recording.

Predication names a Buffer, offset and operation. Clearing predication passes a null Buffer. Query and
predication operations do not add barriers or waits; the caller supplies synchronization through the
ordinary barrier and Queue mechanisms.
^rhi-que-009

### RHI-QUE-007 — Canonical initial synchronization facts

Every Buffer and Texture exposes immutable initial synchronization facts established by its creation
path: initial `PipelineSync`, `ResourceAccess`, and for Textures an initial `TextureLayout`. A
swapchain image exposes the corresponding facts for its current acquisition sequence.

These facts are the starting point for explicit barriers. They do not represent hidden tracked global
state shared between independent command recordings. Validation may track submitted facts to diagnose
incorrect use, while the direct backend encodes exactly the caller-provided barriers.
^rhi-que-007

### RHI-QUE-005 — Explicit barrier ordinals

The public barrier inventory is closed and concrete:

- `MemoryBarrier` for memory ordering without naming a resource;
- `BufferBarrier` and `TextureBarrier` for one resource/range;
- `AliasingBarrier` for before/after resource aliasing;
- `QueueRelease` and `QueueAcquire` for explicit cross-Queue ownership handoff.

`PipelineSync` names execution scope, `ResourceAccess` names real memory access and `TextureLayout`
names portable image usage. `BarrierPhase` is `Complete`, `Begin` or `End`; split state is an operation
property, not a fake Pipeline stage.

D3D12 COMMON and Queue-specific native layout choices remain inside the D3D12 mapper. Portable
`TextureLayout.General` is the backend-neutral general-purpose layout.
^rhi-que-005

### RHI-QUE-008 — Memory and aliasing barriers

Memory and aliasing barriers are explicit commands and are never inferred from copies, rendering,
dispatch or resource creation. The backend validates Queue legality and exact resource provenance,
then lowers the public operation either to Enhanced Barriers or the legacy D3D12 path.

A Queue release/acquire pair is matched by the caller's synchronization plan. The RHI does not create
a hidden completion or wait to repair a missing handoff. Validation diagnoses mismatched split or
Queue-transfer facts before forwarding valid calls.
^rhi-que-008

### RHI-QUE-006 — Exact state-suppression policy

Only setters whose native effect is fully represented by the current command-state shadow may be
suppressed. Equality is exact for the public value domain, including bitwise floating-point policy
where required.

Draw, dispatch, copy, clear, barrier, query, event, bundle, indirect, DXR, Work Graph and presentation
operations are never suppressed as future-state setters. Pipeline switches invalidate only the state
that the native API invalidates. Descriptor heap or root-signature changes invalidate the affected
native binding shadow without inventing a public compatibility value.

All state arrays and capture storage are prepared before native encoding. At stable high-water
capacity, repeated state-setting and command recording perform no managed allocation, reflection walk
or string lookup.
^rhi-que-006
