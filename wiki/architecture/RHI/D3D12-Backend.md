# Direct3D 12 Backend Contract

### RHI-D3D12-001 — Fixed native boundary

The backend uses Silk.NET Direct3D12/DXGI and exact Agility SDK assets; Vortice and secondary shader
inspection runtimes are absent. The common assembly does not depend on Windows/Silk. Device creation
materializes one immutable capability/limit/format snapshot, and unavailable DeviceCapability objects are
not returned. D3D12 is one backend, so native enum values and queue restrictions never leak into the
portable type system except through the explicit native-access capability. Every portable enum and
description that reaches D3D12 is handled by an exhaustive product mapping table or encoder; numeric
enum casts, default fall-through mappings and silently accepted unknown flag combinations are
forbidden.

The available native interface surface follows the pinned header/runtime boundary in
[[Implementation#RHI-IMPL-001 — Product modules and dependencies]]. Runtime availability alone never
causes an unbound Agility interface to appear as an available capability.

The executable mapping owners are divided by native concern:

| Portable concern | D3D12 owner |
|---|---|
| Format, permitted views, aspects and planes | One format-family table selects resource, SRV/UAV/RTV/DSV and copy-compatible `DXGI_FORMAT` values. |
| Buffer/Texture/Heap descriptions | Resource dimension/flags, allocation class, alignment, canonical public initial access/layout and node masks are mapped together; invalid native creation is reported by the actual creation call. |
| Buffer barriers | One public barrier always addresses the complete Buffer; Enhanced Barrier encoding uses offset zero and the complete byte size or `UINT64_MAX`. |
| Memory and aliasing barriers | `MemoryBarrier` maps a no-resource/no-layout access pair to the Enhanced native memory barrier or the conservative `ResourceBarrier` equivalent; `AliasingBarrier` maps the caller's exact before/after resource spans. See [[Queue-and-Commands#RHI-QUE-008 — Memory and aliasing barriers]]. |
| Views and descriptors | CBV/SRV/UAV/RTV/DSV fields are encoded from the resource's declared format family, range and usage; a view cannot widen creation-time permissions. |
| Copy and resolve | D3D12 subresource/plane rules and `GetCopyableFootprints` own footprints, row pitches and required byte counts; compressed blocks are never recomputed by an independent RHI formula. |
| Rendering and queries | Attachment load/store/resolve intent and each Query type map to an exact native operation/query-heap type/resolve stride. |
| Stream output | The portable state maps to `D3D12_STREAM_OUTPUT_DESC`; ordinary elements read S# semantic identity and explicit gaps use a null native semantic. |
| Presentation | DXGI format, color space, tearing, frame-latency wait, resize and present flags are selected from `SwapchainDesc`, `SwapchainConfig` and the immutable Device/Output snapshot. |

Adding a public enum member or operation requires an explicit product mapper branch and an ordinary
legality/mapping test; no separate planning inventory stands between the public API and backend code.
^rhi-d3d12-001

The D3D12 creation encoder consumes the public initial facts in
[[Queue-and-Commands#RHI-QUE-007 — Canonical initial synchronization facts]]. Buffers have no portable
Texture layout. Enhanced-barrier Texture creation uses the corresponding native initial layout;
ordinary undefined-content Textures use `D3D12_BARRIER_LAYOUT_UNDEFINED`. The `ResourceBarrier` API
path selects the legal native creation state that represents the same public starting condition and
encodes the caller's first public barrier from that condition; this is mapping an existing barrier,
not inserting one. Upload and readback Buffers obey D3D12's fixed `GENERIC_READ` and `COPY_DEST`
creation restrictions while reporting the portable memory-type-specific access. Swapchain acquire
reports `Present` for preserved DXGI back buffers, and Present requires the public image to have been
explicitly returned to the Present condition.

### RHI-D3D12-002 — Barrier encoding and cross-Queue handoff

QueueRelease and QueueAcquire are asymmetric. For enhanced texture barriers, the source releases
`Before` access/layout into the queue-independent `COMMON` layout with `NO_ACCESS`; after the caller's
explicit timeline wait, the destination acquires from `COMMON`/`NO_ACCESS` into `After`. Buffers use the same
access deactivation/activation without a layout. Queue-specific layouts never cross to an
incompatible Queue. On a Device using the `ResourceBarrier` encoder, D3D12 performs the
source-state-to-COMMON and COMMON-to-destination-state operations around the same explicit wait. The
RHI neither duplicates one Before/After transition on both sides nor inserts the wait.

The portable Sync/Access/Layout barrier model is core RHI behavior, not an optional D3D12 feature.
The backend selects Enhanced Barriers or `ResourceBarrier` from its immutable Device snapshot. A
Device lacking Enhanced Barriers remains a valid D3D12 backend; only an operation whose semantics
cannot be represented by the selected native path is unavailable through its owning named
capability. Public barrier ordinals and caller-authored synchronization remain identical.
Enhanced Barrier availability is not a public `DeviceFeatures` bit or DeviceCapability. It selects
the D3D12 backend's native encoder for this same contract. Native render-pass and pipeline-library
support are likewise internal encoding/cache choices; BeginRendering and PipelineCache remain
available through their portable contracts.

For [[Queue-and-Commands#RHI-QUE-008 — Memory and aliasing barriers]], Enhanced D3D12 maps
MemoryBarrier to `D3D12_GLOBAL_BARRIER`; “global” is only D3D12's native name for a no-resource
memory-access barrier. Enhanced AliasingBarrier emits the necessary before-resource
flush/deactivation and after-resource discard/activation barrier groups. The `ResourceBarrier`
encoder uses a null UAV barrier for UAV/acceleration-structure memory ordering, a conservative
null/null aliasing barrier for other supported memory-only pairs, and native aliasing barriers for
AliasingBarrier before the caller's separate first-use transitions. Every necessary 1:N native
sequence retains the public ordinal and reports why more than one native call was required.
^rhi-d3d12-002

### RHI-D3D12-003 — Root artifacts and pipelines

One deterministic D3D12 root-layout compiler consumes the exact linked/specialized S# Program layout,
selected entry-point layouts, immutable Device snapshot and node mask. It never consumes a caller
slot table or reparses DXIL. Slang register spaces, binding indices, array counts and parameter-block
boundaries are preserved. Resource ranges and sampler ranges occupy their respective descriptor
tables; an unbounded range is last in its table; bindless ranges address the Device's stable global
descriptor heaps. A non-empty top-level ordinary-data constant buffer uses one root CBV, reflected
inline constant data uses root constants, and all other CBV/SRV/UAV bindings use tables. A sampler
becomes static only when Slang marks it immutable and supplies its complete state; every runtime
Sampler uses the sampler descriptor table.

The canonical immutable marker is Slang's user-defined `ImmutableSampler` variable attribute as
bound by S# `ImmutableSamplerReflection`; the backend never looks up that attribute name. Its exact
twelve arguments are min/mag/mip filter, U/V/W address mode, mip-LOD bias, maximum anisotropy,
comparison mode, static border color, minimum LOD and maximum LOD. Integer values use the S#
`SlangSamplerFilterMode`, `SlangSamplerAddressMode`, `SlangSamplerComparisonMode` and
`SlangStaticSamplerBorderColor` ordinals; LOD values and bias are finite 32-bit floats. S# retains
the sampler's register, space, declaration count and field identity in its binding range, attaches
the typed immutable state there, and omits it from the runtime `BindingElements` sequence. D3D12
accepts only a scalar immutable sampler because a native static sampler cannot represent an indexed
sampler range; an attributed Slang sampler array is a root-artifact `PipelineCreation` failure rather
than silently becoming a runtime sampler. A non-attributed sampler, including every sampler array,
remains a runtime sampler descriptor range with its exact reflected count.

Root parameters use a canonical order derived from binding role, register space, register class,
base register and visibility. Visibility is the exact single D3D12 stage only when all consumers fit
that visibility; otherwise it is ALL. Root Signature 1.1 flags follow the actual native mutation
promise, not the public persistent/transient category. Every published Bindless/persistent range and
every per-recording descriptor range is immutable until all commands that address it retire, so those
ranges do not use `DESCRIPTORS_VOLATILE`. The memory named by ordinary CBV/SRV/UAV descriptors is
`DATA_VOLATILE` unless the RHI itself owns immutable per-recording storage. Such context-private
ordinary-data storage may use `DATA_STATIC`; `DATA_STATIC_WHILE_SET_AT_EXECUTE` is used only where the
implementation guarantees that every intervening write causes the required rebind. UAV data remains
volatile by default, sampler ranges receive no resource-data flags, and a root CBV follows the same
actual data-storage rule. There is no separate root-budget preflight: the deterministic layout is
serialized once and an over-budget or otherwise invalid signature is reported by that authoritative
serialization/creation result.

Global and ray-tracing local root signatures are compiled separately from their corresponding S#
layouts. `RayTracingShaderTable` record layout is derived from the local layout and S# entry-point
identity; ray-generation, miss, hit-group and callable records retain their distinct D3D12 size and
stride rules. Root-layout compatibility and every pipeline/cache key include the canonical parameter
sequence, range flags, static samplers, node mask, Slang program/specialization identity and the
root-layout compiler schema version. Native serialization and creation failures retain Slang/native
diagnostics in `GraphicsException`.

The graphics pipeline maps
[[Core-Contract#RHI-CORE-008 — Stream-output pipeline description]] directly to
`D3D12_STREAM_OUTPUT_DESC`. An ordinary `StreamOutputElement` becomes one
`D3D12_SO_DECLARATION_ENTRY` using the referenced `VariableLayoutReflection.SemanticName` and
`SemanticIndex`; `StreamOutputElement.Gap` uses `SemanticName = null`. Stream, start component,
component count, output slot, buffer strides and rasterized stream are copied exactly after range
validation; a null public rasterized-stream index maps to `D3D12_SO_NO_RASTERIZED_STREAM`. UTF-8
semantic storage lives only through the native pipeline-creation call. The backend does not cache or
expose another semantic table.
^rhi-d3d12-003

### RHI-D3D12-004 — Direct native getters

D3D12-native getters return borrowed values directly. The optional Validation Layer diagnoses wrong
backend/Device, disposed object, invalid node or illegal Context state before forwarding; the direct
backend path treats them as caller preconditions and contains no validation branch. There is no
Try/Unchecked twin and no failure sentinel write. Command-list borrow is valid only during Recording
and until the next public Context/capability call. Automatic retirement strongly retains every
resource in the caller-supplied list; Manual retirement retains no such list and makes the
caller responsible for every object referenced through the native pointer. Success dirties the full
relevant state shadow in both modes. Stack-only types cannot prevent a caller from illicitly caching
a raw pointer, so post-lifetime pointer use remains a contract violation.

The same boundary applies whenever a backend operation receives a public `Buffer`, `Texture`, View or
other shallow resource base. Those abstract bases have non-public construction, and only the owning
backend may create its internal sealed derived identities. One internal
`BackendResourceCast<TResource>.From` call appears at the use site inside the one backend method. A
development/checked build compiles that call as a normal safe reference cast and rejects the wrong
derived type. A shipping build compiles the same call as `Unsafe.As<TResource>`. The choice is made by
one build symbol/source-generation condition; it is not a runtime value and does not create
Checked/Unchecked methods, a second backend or a wrapper dispatch. Shipping IL at that site contains
no cast-mode field/load/branch, `castclass` or validation call.

The optional `ValidationLayer<TBackend>` is independent from that build choice. When selected at
startup it additionally checks exact Device/backend-runtime provenance and live state before
forwarding. The checked build and the layer improve diagnostics; all valid inputs still reach the
same backend method and native implementation and produce identical commands. The construction
invariant plus either the layer's successful check or the shipping caller precondition makes the
unchecked conversion valid.
^rhi-d3d12-004

### RHI-D3D12-005 — Exclusive native Queue lock

Raw `ID3D12CommandQueue*` access requires a stack-only `D3D12CommandQueueLock` holding the same
private Queue mutex as Submit and sparse mapping. All struct copies observe one non-reusable shared lock
sequence. Disposing any copy unlocks once without throwing; repeated Dispose is idempotent;
`IsHeld` becomes false and
Pointer becomes unavailable for every copy. Calling Submit, UpdateSparseMappings or another RHI
method that needs that Queue while the lock is held is forbidden. Lock sequence values neither wrap
nor reactivate an earlier lock. A default value has `IsHeld=false`, its Pointer getter throws
`InvalidOperationException`, and Dispose is a no-op. `LockCommandQueue` may wait only for another CPU
owner of that Queue lock; it never waits for QueueCompletion or Device idle. The optional Validation
Layer diagnoses same-thread re-entry before it could deadlock; the direct path treats non-reentry as
a caller precondition.
^rhi-d3d12-005

### RHI-D3D12-006 — Native command encoding

Private helpers may group inputs and perform necessary 1:N native encoding, but they preserve every
public ordinal and synchronization meaning. The Wiki does not prescribe generic helper verbs that
would conflict with the public Buffer `Map` operation.
Rendering, copy/resolve/clear/query and submit/present encode their native operation semantics; only
normalized future-state setters may be elided under
[[Queue-and-Commands#RHI-QUE-006 — Exact state-suppression policy]].

`ID3D12Resource::Map` returns the subresource base pointer rather than offsetting it by the supplied
read range. Buffer mapping therefore resolves `BufferRange.Whole`, checks the `Int32` Span limit,
calls native Map, adds the resolved byte offset and only then constructs `MappedBuffer.Bytes`. Native
Flush/Invalidate/Unmap mapping follows [[Lifetime-Concurrency-and-Diagnostics#RHI-LIFE-006 — Mapping lifetime]].
^rhi-d3d12-006

### RHI-D3D12-007 — Capability availability and native calls

`DeviceFeatures` is the coarse required/optional set consumed during Device creation. The concrete
capability objects, public operations, Queue order and retained objects are defined once in
[[Advanced-Capabilities]]. D3D12 fills them from the pinned interface surface and immutable feature
snapshot below; it does not create a second support pass or a document-driven feature table.

| Public capability | D3D12 requirement and native owner |
|---|---|
| `SparseResources` | Tiled Resources/Resource Binding tiers plus per-format tiled-resource queries; mapping uses `UpdateTileMappings`/`CopyTileMappings` on the selected Queue. |
| `SamplerFeedback` | `D3D12_FEATURE_DATA_D3D12_OPTIONS7.SamplerFeedbackTier`, format support and feedback-map alignment; clear/resolve use the sampler-feedback descriptor and resolve modes. |
| `Residency` | `ID3D12Device3::EnqueueMakeResident`, `ID3D12Device::Evict` and DXGI memory-budget queries must all be bound for the corresponding operation. |
| `RayTracing` | `D3D12_FEATURE_DATA_D3D12_OPTIONS5.RaytracingTier`, state-object interfaces and separately queried optional DXR operations; pipeline/state-object creation remains the authoritative native check. |
| `MeshShaders` | `D3D12_FEATURE_DATA_D3D12_OPTIONS7.MeshShaderTier`, mesh-stage limits and the required command-list interface. |
| `VariableRateShading` | `D3D12_FEATURE_DATA_D3D12_OPTIONS6` supplies the tier, rates, per-primitive support and image tile size. |
| `WorkGraphs` | `D3D12_FEATURE_DATA_D3D12_OPTIONS21.WorkGraphsTier`, state-object properties and command-list interfaces. Because Work Graphs are in the first-delivery scope, a missing Silk binding is extended/upgraded during implementation rather than reported as hardware absence. |
| `IndirectCommands` | The Device's available command-signature/ExecuteIndirect surface plus separately reported mesh/ray/work-graph indirect support; unsupported argument types never enter a native signature. |
| `CalibratedTimestamps` | `ID3D12CommandQueue::GetClockCalibration` and `GetTimestampFrequency` for the exact Queue. |
| `LinkedAdapters` | `ID3D12Device::GetNodeCount` and creation/visibility node-mask rules. |
| `ExternalResources` / `ExternalTimelines` | Shared-handle support for the exact resource/Heap/fence type and `OpenSharedHandle`/shared-handle creation. Queue completion fences remain private. |
| `D3D12NativeAccess` / `D3D12Diagnostics` | Only bound D3D12 interfaces and the native debug/DRED/tool facilities actually enabled for the selected build/runtime. |

Bundles and QueryPool are core object families, not entries in this table. Bundle legality is the
closed command inventory in [[Queue-and-Commands#RHI-QUE-004 — CommandContext and RecordedCommands]].
Query mapping follows [[Queue-and-Commands#RHI-QUE-009 — Queries and predication]] and uses the
exact native heap type, Queue compatibility and resolve stride recorded by the created QueryPool.
Platform-specific limitations
remain visible through the specifically named capability and its limits instead of forcing a future
backend to pretend it has the same native mechanism.

All capability families in this table are implementation scope for the D3D12 backend. On a
particular Device, the capability may correctly be absent because its immutable native feature query
reports no hardware/driver support. It may not be absent merely because the backend omitted the
implementation or the pinned binding lacks a required interface; the latter requires extending the
binding or changing the exact pin and rerunning native integration tests.
^rhi-d3d12-007

The native barrier and root-range constraints above follow the
[D3D12 Enhanced Barriers specification](https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html)
and [Root Signature 1.1 contract](https://learn.microsoft.com/en-us/windows/win32/direct3d12/root-signature-version-1-1).
