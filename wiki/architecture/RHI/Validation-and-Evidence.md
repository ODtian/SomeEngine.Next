# RHI Validation and Implementation Acceptance

## Validation boundary

Validation is an optional layer around the normal RHI call surface. It is not code sprinkled through
the shipping backend behind an `Enabled` flag, and it is not a second backend implementation.

### RHI-VAL-001 — Pluggable Validation Layer

`SomeEngine.Graphics.Validation` supplies a `ValidationLayer<TBackend>` that forwards to the same
`TBackend : class, IGraphicsBackend` used without validation. The application chooses once while it
creates the graphics runtime:

- validation disabled: the closed receiver contains the concrete backend directly;
- validation enabled: the closed receiver contains `ValidationLayer<TBackend>`, which holds that same
  concrete backend and validates before forwarding;
- interface users may keep either object as `IGraphicsBackend`; generic users preserve the selected
  closed type along the behavior-execution call chain.

The validation assembly is optional and the base RHI and D3D12 backend do not reference it. Disabling
validation means that no Validation Layer object is constructed. Changing the setting recreates the
graphics runtime just as changing the backend does; it does not toggle validation inside live command
recording.

The layer owns its validation-only state mirrors, messages and message sink. Its construction always
transfers ownership of the wrapped backend to the layer; there is no borrowed Validation wrapper and
no ownership option. It never creates a second native Device, Queue, resource or pipeline, and it
forwards every valid call without changing barriers, submissions, descriptor generations,
native-call suppression or return values.

The top-level receiver is the only object its caller disposes. A direct receiver owns its concrete
backend; a validated receiver owns `ValidationLayer<TBackend>`, which in turn owns and disposes that
same concrete backend. The wrapper and wrapped receiver converge on the backend runtime's one
terminal/idempotent Dispose gate. Constructing another owning receiver around the same backend is a
caller contract violation, not a reason to add a borrowed constructor, ownership flag, public
reference count or ownership annotation.
^rhi-val-001

This follows the established layer shape rather than a project-specific verification mechanism:
[NVRHI exposes validation as an optional library/layer](https://github.com/NVIDIA-RTX/NVRHI), and
the [Vulkan Loader](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderInterfaceArchitecture.md)
loads explicitly enabled layers into the call chain instead of putting an enable branch in every
driver operation.

### RHI-VAL-002 — Zero validation overhead when absent

On the direct backend path there is no validation assembly call, wrapper dispatch, validation-mode
field load, nullable-layer test, message-sink test, validation branch, validation callback, diagnostic
allocation, reflection walk or validation-only state update. A `static readonly` flag that the JIT is
expected to fold is not sufficient. The direct closed generic receiver remains eligible for the same
devirtualization and inlining whether or not the optional validation assembly is present elsewhere in
the process.

“Zero validation overhead” does not remove work that implements the requested operation. Native
result handling, state transitions, lifetime retention, native argument construction and checks
required to keep a safe managed/native representation remain part of the base RHI. Their cost is RHI
functionality, not disabled diagnostics.
^rhi-val-002

### RHI-VAL-003 — Rule for every check

Every proposed check is assigned by what changes when it is removed:

1. It stays in the base RHI only when a valid call needs its result to choose or construct the native
   operation, to preserve a public state transition or atomicity guarantee, to retain live native
   dependencies, to interpret a native result/terminal state, or to prevent an out-of-bounds or
   malformed managed/native representation where the native API provides no usable failure result.
2. It belongs only to the Validation Layer when all valid-call results are unchanged without it and
   its sole purpose is to diagnose a caller contract violation more clearly.
3. It is deleted when it merely predicts the immediately following real operation, repeats shader or
   native reflection, and provides no independent capability choice or recovery information.

This test is applied to the whole RHI, not only to shader programs.
^rhi-val-003

## Complete check-placement audit

| Area | Base RHI: required for execution | Validation Layer only | Deleted duplicate/preflight work |
|---|---|---|---|
| Backend/resource identity | Public resource bases have non-public construction; backend children are internal sealed identities. At the same use site a checked/development build compiles a safe cast and shipping compiles `Unsafe.As<T>`, with no runtime selector. | The optional layer independently checks exact Device/backend-runtime identity and live state before forwarding. | Runtime cast-mode fields, a conditional cast branch in shipping, checked/unchecked twin APIs or treating the optional layer as the compile-time cast mechanism. |
| Device and native failure | HRESULT/status interpretation, Device removal publication, the D3D12 fence removal sentinel and terminal-state propagation. | Extra object labels, live-object reports and detailed call-history messages. | A second call that predicts the same native creation result. |
| Queue submission | The Queue gate, native-acceptance boundary, input consumption, completion value allocation and retention of intrinsic payload after partial native acceptance. Automatic mode also retains every referenced public object. | Cross-object association explanations, external timeline monotonicity and forbidden re-entry diagnostics. | A dry-run submit validator or hidden synchronization repair. |
| Command recording | Slot ownership, reset/close result handling, initial reservation and pooled growth of resource/sampler descriptors, failure-atomic capacity exhaustion, intrinsic native-payload transfer and one captured native descriptor generation. Automatic mode additionally captures public dependencies; Manual mode does not. | Begin/End/rendering-scope legality, thread ownership, command-family misuse, use-after-dispose and Manual-mode early-lifetime diagnostics. | Per-command reflection, caller-computed hard capacity limits or a second command stream used only to check the first. |
| Barriers | Exhaustive Sync/Access/Layout mapping, required 1:N native expansion and caller ordinal preservation. | Hazard history, incorrect Before state, missing release/acquire/wait and Queue-ownership diagnostics. | Barrier inference, repair, merge, deduplication, reordering or a base-RHI state tracker that exists only to check caller barriers. |
| Queries | Native heap/type/index encoding, range arithmetic and result stride. Automatic mode captures the public pool/destination dependency; Manual mode leaves their lifetime to the caller. | Begin/End/write/resolve lifecycle, active-range and early-index-reuse diagnostics. | A separate query preflight pass. |
| Descriptors | Slot bounds needed to address storage, Resource-versus-Sampler table selection, generation publication, heap growth, native-generation retirement and Automatic-only retention of each published view/resource/counter/Sampler/AccelerationStructure. | Wrong Device, incompatible view usage, unpublished bindless use and premature logical/physical index reuse diagnostics, including Manual lifetime violations. | A duplicate descriptor model or reflection table. |
| Bindings | S# layout data actually used to materialize descriptors/root arguments, descriptor copies and root setters. | Parameter-block compatibility, missing/duplicate field, resource-usage and lifetime diagnostics. | Independent shader metadata, DXIL reflection or a second layout/version system. |
| Resources, views, copy and resolve | Canonical initial synchronization facts, imported-resource current-state input, size/offset/stride/overflow and format/plane/block calculations needed to construct native structures; creation-time view restrictions required because D3D12 descriptor creation has no HRESULT. | Usage/state compatibility, wrong Device, overlap and lifetime diagnostics not needed for address/descriptor construction. | Caller-selected arbitrary creation states and `CanCreate*`/`Validate*` calls that immediately repeat the real creation. |
| Mapping | `Whole` resolution, the `Int32.MaxValue` Span boundary, one active public mapping, absolute contained ranges, shared non-reusable sequence, adjusted Span construction and unmap-once behavior. | Thread ownership and clearer stale-copy diagnostics beyond the required sequence result. | A second checked-memory wrapper in shipping. |
| Presentation | Acquire sequence, explicit acquired-image initial state, swapchain generation, commit boundary, wrapper invalidation, timeout conversion and DXGI result handling. | Wrong Queue/Device/image ownership and misuse diagnostics that do not drive the state machine. | An acquire/present/reconfigure dry run or a disposable acquired-image operation that silently commits/abandons presentation. |
| Retirement | One immutable Device `RetirementType`, the common terminal/idempotent Dispose gate, structural parent cascade, Automatic dependency capture and completion-based physical release; Manual performs no automatic per-use tracking. | Unused Manual capture reservations, early dispose/reuse, outstanding native use, live descendants and incomplete wait diagnostics; reporting never cancels cleanup. | Per-object/mutable retirement modes, public reference counting, ownership annotations, finalizer-driven native lifetime or type-specific retryable Dispose states. |
| Pipeline cache | Bounds, integer overflow, corruption and compatibility parsing for caller-supplied bytes; deterministic key lookup and native cache result handling. | Detailed reason reporting and cross-run diagnostics. | A pre-implementation reference codec or parallel cache parser. |
| Sparse mapping and residency | Range/tile arithmetic needed to form the native call, the Queue gate, native acceptance, completion signaling, and Automatic-mode retention named in RHI-CAP-002/RHI-CAP-004. | Mapping overlap/use hazards, unmapping before prior use finishes, budget advice and early Manual disposal. | Hidden Queue waits for sparse mapping, automatic mapping from resource use, or automatic MakeResident/Evict from binding. |
| Ray tracing and Work Graphs | S#/Slang layout actually needed to build state objects/records, native size/alignment queries, caller-supplied scratch/backing ranges, explicit initialization and native result handling. | Geometry/layout/record compatibility, missing caller barriers, reuse hazards and clearer subfeature-limit messages. | DXIL re-reflection, a duplicate shader table, hidden scratch/backing allocation, or a program-support dry run. |
| Mesh, VRS, feedback and indirect commands | Immutable reported tier/limits, exact native argument construction and the command/state effects defined in RHI-CAP-003/RHI-CAP-007/RHI-CAP-009. | Exceeding an advertised limit, incompatible feedback pairing, wrong shading-rate image and indirect-layout compatibility diagnostics. | Shader/CPU emulation of an unavailable tier, hidden feedback readback/mapping updates or CPU expansion of indirect work. |
| External objects, linked adapters and timestamps | Exact handle ownership/open/export, required import synchronization facts, private completion-fence protection, node masks and one native calibration sample. | Wrong handle type, wrong node visibility, stale external value and profiler-use guidance. | Exporting/user-signaling a private completion fence, rebasing an imported timeline or inventing a generic Node object. |
| Capability discovery | Device creation queries and immutable capability/limit/format snapshot values that let the caller choose a genuinely different path. | Diagnostics for using an absent capability or exceeding a reported limit. | Rechecking the same capability at every hot call. |
| State suppression | The native state shadow and normalized equality that actually suppress redundant native setters. | Reports explaining why a setter was or was not suppressed. | A second signature catalog outside product code. |
| Threading | Locks/atomics required by a method documented as thread-safe, gates required for Queue/native ownership, and the cold terminal synchronization that makes concurrent Dispose calls collectively release once. | Calls that violate an externally synchronized contract, including normal use racing with Dispose. | Runtime annotation scanning or validation-only thread-state reads on the direct path. |

The important distinction is semantic, not whether a condition is written with `if`, `Debug.Assert`
or an exception. Moving a diagnostic condition into the base backend under a runtime option still
violates this boundary.

### RHI-VAL-004 — Pipeline creation is authoritative

The RHI has no separate shader-program preflight operation or persistent diagnostic-store identity.
Pipeline creation already has to consume S#/Slang reflection and layout to build root artifacts,
then call root-signature serialization and the relevant D3D12 pipeline/state-object creation
function. A separate pass would repeat that work and still could not replace the authoritative
native result.

This does not remove real discovery APIs. `TryGetCapability<TCapability>`, immutable Device limits,
format support and sample-count support return information with which a caller can select a different
algorithm, format or capability before attempting creation. They therefore have an independent
result and remain part of the RHI.
^rhi-val-004

### RHI-VAL-005 — Native validation follows the selected platform

When the Validation Layer is selected at startup, every available D3D12 native diagnostic facility
is enabled by default: debug layer, GPU-based validation, synchronized Queue validation and DRED.
Explicit `D3D12ValidationOptions` may disable an individually named expensive facility, but there is
no second implicit "validation selected but native validation off" default. A future Vulkan backend
explicitly enables the requested Vulkan validation layers. The portable layer presents common
messages where possible but does not force identical platform features. With validation disabled,
none of those optional native validation facilities are enabled.
^rhi-val-005

## Implementation acceptance

Acceptance is performed by normal product tests and benchmark executables after the product exists.
The plan does not ship a reference implementation, custom document verifier, JSON contract format or
generated API catalog. Compilation establishes the real API; unit/integration tests establish
behavior; native runs establish D3D12 behavior; benchmarks establish allocation and timing.

### RHI-EVID-001 — Required behavior coverage

The product tests cover Submit acceptance and partial native failure, completion sentinel limits,
RecordedCommands copy/submit/dispose behavior, initial command-recording reservations, pooled
high-watermark growth and failure-atomic exhaustion, descriptor-generation overwrite and retirement,
canonical creation/import/acquire synchronization facts, explicit Present preconditions, swapchain
acquire/reconfigure races, Mapping and native Queue-lock copies, Device loss, query families,
pipeline-cache corruption/merge, every capability in [[Advanced-Capabilities]] and every
portable-to-D3D12 mapping family.
Barrier coverage includes every Sync/Access/Layout value, `MemoryBarrier`, whole-Buffer and
subresource Texture barriers, asymmetric AliasingBarrier resource spans, QueueRelease/QueueAcquire,
the Enhanced and `ResourceBarrier` encoders, and ordinal-preserving 1:N cases. It asserts that no
public barrier is inferred, repaired, merged, deduplicated, suppressed or reordered.
Mapping coverage includes `Whole`, non-zero windows, `Int32.MaxValue` rejection, absolute
Flush/Invalidate containment and copied-value disposal. Lifetime coverage applies one matrix to every
disposable RHI family: first/repeated/concurrent Dispose, parent cascade, child-after-parent Dispose,
normal-use races, Manual versus Automatic retirement, device-loss teardown and root backend switch.
The retirement test backend observes that both modes retain intrinsic execution payload, that only
Automatic mode strongly retains public dependencies through completion, and that a valid Manual
caller can release them only after its own wait. Capability tests include sparse map/unmap/copy
ordering and retention; residency completion and Evict preconditions; feedback clear/resolve;
acceleration-structure build/update/copy/compact/serialize; shader-table snapshot and dispatch;
mesh/VRS tier boundaries; Work Graph first-use/reinitialize/preserve and backing-memory reuse;
indirect argument families; linked-node visibility; external handle/timeline ownership; calibrated
timestamp Queue affinity; and D3D12 native access/diagnostics availability. Receiver tests cover
exactly one owning root for
direct interface, generic and validated construction, including idempotent cascade into the one
backend-runtime Dispose gate.

Pipeline coverage includes ordinary and gap stream-output elements, S# semantic name/index use,
strides, rasterized stream and cold UTF-8 lifetime. Descriptor coverage includes every ordinary/
Bindless inheritance pair, explicitly including
`BindlessAccelerationStructureSrv : AccelerationStructureSrv`, and proves that the Bindless form is
the same immutable descriptor identity when used through its ordinary base.

Validation integration runs valid calls through both the optional layer and direct backend and
compares native behavior. It also checks that a representative shipping resource conversion has the
unchecked `Unsafe.As<T>` shape with no mode load/branch/castclass, that the checked build uses the
normal safe cast at the same source location, and that the optional layer rejects wrong
Device/backend-runtime provenance before forwarding; these are implementation variants of one API,
not public cast modes. Real
graphics integration covers Slang module load, entry lookup, composite/link/specialize,
reflection/layout/metadata, DXIL emission and D3D12 root-signature/pipeline/state-object creation.

WARP supplies deterministic core coverage. NVIDIA, AMD and Intel runs cover advertised hardware
capabilities, presentation and performance; an unavailable external machine is reported as an
unexecuted environment, not counted as a pass.
^rhi-evid-001

### RHI-EVID-002 — Workload equivalence

RHI, Direct Silk and C++ D3D12 runners use the same Slang-produced DXIL, root-layout meaning, resource
dimensions/formats/initial contents, draw/dispatch/copy/resolve/query work, logical pass dependencies,
Queue topology, descriptor/resource set, presentation settings, seed and final output hash. Direct
Silk and C++ may hand-optimize barriers, passes, descriptors, root binding and native calls; they do
not have to imitate the RHI call sequence. They must not add or omit observable GPU work.

The RHI generic and interface receivers use the same backend and identical public input, so their
native command and barrier sequences must match exactly. Every public barrier keeps its ordinal; a
necessary 1:N D3D12 expansion records its reason. No comparison requires the RHI native trace to be
identical to Direct Silk or C++.
^rhi-evid-002

### RHI-EVID-003 — Fixed performance protocol

Shipping Release, win-x64, validation/DRED/capture disabled; 8,192 warm-up frames; 16,384 measured
frames in each of five independent processes per variant; no sample rejection; R-7 P50/P95/P99.
R-7 means the repository's existing linear-interpolation percentile implementation and golden values;
the graphics benchmark reuses it rather than introducing another statistics implementation.
Stable-frame measurement begins only after shader work, pipelines, persistent bindings, context slots,
descriptor heaps, JIT tiers and driver caches are warm. The timed interval performs no resource
creation, upload, descriptor publication or heap growth.

The benchmark controller runs generic RHI, interface RHI, Direct Silk and C++ D3D12 by interleaved
process round rather than completing every process of one variant first; the deterministic order is
recorded with the raw samples. Every process records the exact executable/build identity, .NET or C++
toolchain, CPU and affinity/priority policy, Windows version and power mode, adapter identity, driver,
Agility version and validation/capture state. A run whose required environment was not established is
reported as unexecuted, not repaired by dropping frame samples.

Each non-empty workload uses one identical CPU interval in all four runners. It begins immediately
before the frame's first `CommandContext.Begin` equivalent and ends immediately after its final
Submit/Present call returns. It includes filling all per-frame packets, command recording, every
caller barrier, End, Submit and Present. It excludes the earlier frame-pacing/completion wait and all
shader/pipeline/resource creation, upload, descriptor publication/growth, readback and result hashing.
The Three-Queue workload includes the Copy, Compute and Graphics recording and Queue calls in this one
interval. Empty Submit measures only construction of its empty submit description, Submit and return
of its completion; it has no GPU timing threshold.

The GPU interval begins at the first workload timestamp on the earliest participating Queue and ends
at the last graphics workload command immediately before Present. Multi-Queue timestamps are mapped
to one time domain using the calibrated timestamp samples for those Queues. Display scan-out,
frame-latency wait and any blocking inside Present are not part of GPU command time; presentation
settings disable vsync and remain identical across runners. The runner-owned result output contains
raw CPU/GPU samples, calibration samples, output hashes and the public/native barrier evidence
required by RHI-EVID-002; summary percentiles are derived from those raw samples. WARP may establish
functional equivalence but never counts as a vendor-hardware performance pass.

Each process owns one raw JSON result. The controller report records the relative raw path, SHA-256,
receiver, process index and interleaved position instead of duplicating all samples into another
monolithic JSON document. Gate evaluation verifies the hash and schedule identity, admits one raw
file at a time, retains only the numeric samples and validation facts needed for aggregation, then
releases the full process document. Re-evaluating a report never starts a worker, and missing,
corrupt, swapped or modified raw evidence fails closed.

Both `GC.GetAllocatedBytesForCurrentThread` and ETW allocation events must report 0 B/events for each
stable-frame workload. CPU limits are applied as both absolute delta and relative delta:

| Comparison | P50 | P95 | P99 |
|---|---:|---:|---:|
| Generic RHI vs Direct Silk | 50 µs / 3% | 100 µs / 5% | 200 µs / 8% |
| Generic RHI vs C++ D3D12 | 100 µs / 5% | 200 µs / 8% | 300 µs / 10% |
| Interface RHI vs generic RHI | 25 µs / 3% | 50 µs / 5% | 100 µs / 8% |

GPU limits against either Direct Silk or C++ are 20 µs/1% at P50, 50 µs/2% at P95 and 100 µs/3%
at P99, together with exact output hashes. Empty Submit uses tighter CPU limits: generic versus Direct
Silk 1/2/4 µs, generic versus C++ 2/3/6 µs and interface versus generic 0.5/1/2 µs at P50/P95/P99.

| Workload | Fixed stable-frame work |
|---|---|
| Empty Submit | One Submit with zero waits, zero external signals and zero RecordedCommands; it still produces a completion. |
| Persistent Draw 10,000 | Two explicit barriers, one render scope, one pipeline/binding set, 10,000 triangle draws, query resolve and one submit. |
| Transient Draw 10,000 | The same draw workload with 10,000 distinct transient parameter packets. |
| State Suppression 10,000 | 10,000 equal pipeline, persistent binding, viewport and scissor sets around 10,000 draws; each native setter occurs at most once while all draws remain. |
| Explicit Barrier 4,096 | 4,096 caller-authored barriers followed by one compute dispatch and submit; no barrier is suppressed or reordered. |
| Three-Queue Present | Copy → explicit wait → compute → explicit wait → graphics → present across Copy, Compute and Graphics Queues. |

The concrete closed receiver is inspected on the selected shipping JIT/NativeAOT configuration only
as a representative devirtualization case; the contract promises eligibility, not universal direct
machine calls. Interface mode remains the intentional indirect-call comparison.
^rhi-evid-003

The benchmark executable also exposes a developer-only `diagnose` command for investigating receiver
position and GPU-frequency bias before the architecture is ready for the expensive fixed protocol.
It runs only Persistent Draw, Transient Draw and State Suppression, retains 10,000 draws per frame,
uses 512 warm-up plus 1,024 measured frames, and schedules four sequential interleaved rounds whose
first four orders are the recorded Latin square. It does not run Empty Submit, Explicit Barrier or
Three-Queue Present. Its report profile, exact workload inventory and Gate diagnostic identify it as
non-certification data; a complete diagnostic is `FunctionalOnly` and can never produce a vendor
performance `Passed` disposition. `evaluate` reads an existing report stream and never launches a
worker. This tooling mode does not modify or partially satisfy RHI-EVID-003.
