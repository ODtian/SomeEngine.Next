# RHI Core Contract

## Boundary summary

SomeEngine RHI is a mature, backend-neutral execution API with explicit resource, command,
synchronization, binding, presentation, cache and modern-GPU capability semantics. Low overhead is a
constraint on that RHI; it is not permission to collapse the boundary into raw native calls.

### RHI-CORE-001 — Portable execution boundary

Direct3D 12 is the first delivered backend. Platform differences remain expressible through the
specific `DeviceCapability` objects in [[Advanced-Capabilities]] instead of being hidden behind a
lowest-common-denominator surface. Resource identity,
descriptor data and render-domain data remain backend-neutral. Backend-specific native access is an
explicit capability.
^rhi-core-001

### RHI-CORE-002 — One backend implementation, two receiver choices

`D3D12Backend` is an ordinary sealed reference object that directly owns its runtime/native state and
implements the common interface. A caller may keep that interface and choose virtual dispatch, or
close `Graphics<TBackend>` over the same object and propagate `TBackend` only along behavior-execution
code. The generic form preserves the selected type and is eligible for target-runtime
devirtualization; it does not promise direct machine calls for every CLR shape. Resource/view/
pipeline identities and domain data do not acquire `TBackend` merely because an execution method
uses them. Their public abstract bases have non-public construction; each backend is the only creator
of its internal sealed derived identities. Runtime backend switching quiesces and recreates the
graphics runtime and its resources.

Startup establishes exactly one owning top-level receiver. An interface caller owns and disposes the
selected `IGraphicsBackend`; constructing `Graphics<TBackend>` instead transfers that receiver's
ownership to `Graphics<TBackend>`. A construction-time local reference does not create a second
disposal owner. With validation selected, the top-level receiver owns
`ValidationLayer<TBackend>`, and that layer owns the concrete backend. The two calling styles select
dispatch shape, not duplicate backend/runtime ownership.

The runtime setting enters the generic path with one ordinary application switch; no alias,
generated non-generic facade or build-time backend selection is involved. The concrete backend name
appears only in that switch. `StartSelectedBackend` and `EngineMain` below are application functions,
not extra RHI entry points:

```csharp
static void StartFromSettings(GraphicsSettings settings)
{
    switch (settings.BackendType)
    {
        case BackendType.Direct3D12:
            StartSelectedBackend(new D3D12Backend(settings.Direct3D12), settings.Validation);
            return;
        default:
            throw new NotSupportedException();
    }
}

static void StartSelectedBackend<TBackend>(TBackend backend, bool validation)
    where TBackend : class, IGraphicsBackend
{
    if (validation)
    {
        using var graphics = new Graphics<ValidationLayer<TBackend>>(
            new ValidationLayer<TBackend>(backend));
        EngineMain(graphics);
        return;
    }

    using var directGraphics = new Graphics<TBackend>(backend);
    EngineMain(directGraphics);
}

static void EngineMain<TBackend>(Graphics<TBackend> graphics)
    where TBackend : class, IGraphicsBackend
{
    // Renderer/pass execution methods infer TBackend from graphics.
}
```

An interface-mode application instead stores the startup-selected backend (or selected Validation
Layer) as `IGraphicsBackend` and calls the same interface members directly. Choosing
`TBackend = IGraphicsBackend` is also legal and intentionally preserves virtual dispatch. The
generic application functions may contain `TBackend`; resource, descriptor, scene, material and
Render Graph data types do not.
^rhi-core-002

### RHI-CORE-003 — RHI and Render Graph automation boundary

Automation follows information ownership, not a blanket preference for either implicit or explicit
calls. A layer performs work automatically when it already owns every required fact, valid callers
have no meaningful policy choice, the result adds no hidden pass/submit/wait/barrier or other GPU
work, and the stable path can remain allocation-free. The base RHI therefore owns canonical creation
state, portable-to-native mapping, range normalization, pooled capacity growth, exact future-state
suppression, descriptor publication, cache lookup, lifetime teardown and retirement. Callers do not
repeat those mechanical facts merely to imitate a native API.

Render Graph owns automation that requires the complete future dependency graph: pass/resource/Queue
analysis, barrier generation and placement, cross-Queue waits and signals, transient lifetime and
aliasing, and legally equivalent culling/merge. A caller that bypasses Render Graph supplies the
resulting pass, barrier, wait, signal and submit intent directly to the base RHI. At that boundary the
RHI preserves the supplied semantics and never infers, repairs, merges, deduplicates or reorders them
from Draw/Dispatch/Copy/Binding use. It may only perform local native encoding and suppress a member
from the closed set of exactly equal, side-effect-free future-state setters.

CPU-cheap work is not automatically harmless: an inserted barrier, Present, submit or Queue wait has
observable GPU semantics even if its managed cost is negligible. Conversely, remembering a resource's
canonical initial condition, resolving `BufferRange.Whole`, growing a retained arena before an encode,
or releasing completed descriptor generations is RHI functionality and should not be pushed onto the
caller. Every automation must be explainable in native traces and must not create stable-frame managed
allocation.
^rhi-core-003

### RHI-CORE-004 — Slang/S# is the sole shader authority

Compile, link, specialize, target/profile/capability selection, reflection, layout and shader
metadata come only from Slang through SlangShaderSharp. If Slang exposes a fact that S# does not yet
bind, S# is extended; the RHI does not create a parallel parser or metadata model. D3D12 consumes
Slang-produced DXIL directly. Callers select optional paths through the named Device capabilities.
Pipeline creation reads the selected linked/specialized program and reflected layout once to
materialize root/pipeline artifacts, then reports an actual Slang, serialization or native-creation
failure through `GraphicsException`. There is no separate public shader preflight result, duplicate
DXIL inspection path, support-check pass or second shader-layout vocabulary.

`VertexAttribute.Location` remains the caller's numeric vertex-input location. Pipeline creation
matches it against S#/Slang reflection; the D3D12 backend uses one private, fixed semantic convention
to build the native input layout and exposes no public semantic string. For ray tracing,
`RayTracingHitGroup.ClosestHit`, `AnyHit` and `Intersection` use S# `EntryPointReflection`; only the
hit-group export remains an RHI-created state-object identity. Work Graph `ProgramName` likewise
remains the state-object lookup identity rather than duplicate shader metadata.
^rhi-core-004

### RHI-CORE-005 — Error transport

Successful hot calls carry no universal result wrapper. Operations expected to succeed return their
value or `void`; rare failures that prevent continuation throw `GraphicsException` with native code
and diagnostic text. Ordinary expected branches use `Try*` plus `out` or a domain `Status`.
The optional Validation Layer reports caller contract violations. The base RHI still rejects values
that cannot be represented safely or are required to complete an operation, using standard
argument/state exceptions. Device loss is a terminal exceptional event, not an extra member added to
every ordinary status enum.

Every Try operation that writes a caller span follows one rule: it always reports the required
element/byte count; false means only that the supplied destination is too small; and every
destination span remains unchanged on false. Invalid arguments, wrong Device, disposed/lost objects
and native failure are not converted to false. `TryGetCapability<TCapability>` is the separate
discovery shape defined by RHI-CAP-001: false means that named Device behavior is unavailable and its
out value is null.

`GraphicsException.Error` uses the closed `GraphicsError` set: DeviceLost, OutOfMemory,
OutOfDescriptors, ShaderCompilation, PipelineCreation and NativeFailure. `NativeCode` preserves an
HRESULT or backend status when one exists, and `Diagnostic` preserves the available Slang/native
message. Unsupported advertised behavior is `NotSupportedException`; caller range/type/state errors
remain standard .NET exceptions. A backend does not translate those caller errors into
`GraphicsError.NativeFailure`, and it does not add operation-specific success wrappers.
^rhi-core-005

### RHI-CORE-006 — Allocation boundary

After high-watermark preparation, stable-frame command encoding, binding, submission and retirement
must allocate 0 managed bytes. Variable-length transient inputs use caller-owned `Span`/
`ReadOnlySpan` and stack-only descriptions where their lifetime requires it. Cold creation,
publication/growth and diagnostic failure paths are measured separately and may allocate only where
their contracts permit.
^rhi-core-006

### RHI-CORE-007 — Controlled vocabulary

One domain concept has one name. In SomeEngine-owned public API:

| Term | Meaning |
|---|---|
| `Type` | formal classification; not Kind/Class/Category/Form |
| `Desc` | complete caller-supplied creation or operation input |
| `Options` | defaultable policy switches |
| `Config` | runtime-reconfigurable subset of an existing object |
| `Info` | resolved read-only snapshot of an existing/native object |
| `Data` | opaque or semi-transparent payload bytes |
| `State` | fixed-function configuration or an actual transition-driving state |
| `Status` | observable lifetime phase or expected operation branch |
| `Capability` | one specifically named optional Device behavior; `Features` is a flags snapshot |
| `Layout` | offsets, sizes, alignment, ranges or packing |
| `Signature` | compatibility identity/contract |
| `Index` / `Id` / `Handle` | table position / stable opaque logical identity / native-OS token |
| `Name` / `Label` | lookup-link-export identity / diagnostic-only text |

Flags enums are plural and members do not repeat the enum name. External BCL, Silk and S# names are
not rewrapped or mechanically renamed. A receiver, namespace or established domain context is not
repeated as a mechanical `Rhi`, `Gpu` or backend prefix. `NodeIndex` is only a `uint` property inside
the linked-adapter or Work Graph descriptions defined in [[Advanced-Capabilities]]; there is no
standalone RHI-wide Node type. Exclusive native Queue access uses `Lock`, and presentation uses
`SwapchainImage`.
Ray-tracing dispatch consumes a `RayTracingShaderTable` whose records come from S#/Slang entry-point
and parameter layout. Product API names follow this vocabulary directly; the plan does not maintain
a second symbol catalog or a regular-expression naming standard.
^rhi-core-007

### RHI-CORE-008 — Stream-output pipeline description

Stream output is a graphics-pipeline description, not a separate shader metadata authority.
`StreamOutputState` is a cold-path `ref struct` over caller-owned spans of `StreamOutputElement` and
buffer strides. An ordinary element references the exact S# `VariableLayoutReflection` that identifies
the linked/specialized output variable; pipeline creation reads its `SemanticName` and
`SemanticIndex`. The caller supplies only stream, start component, component count and output slot,
plus the state-level `ReadOnlySpan<uint>` strides and nullable `uint` rasterized-stream index. A null
rasterized stream means that no stream is sent onward to rasterization. The RHI exposes no duplicate
semantic string, substring/offset arena or independently entered semantic index.

`StreamOutputElement.Gap` is the explicit no-variable variant. It represents components that advance
the output stream without writing shader data and does not require a fake semantic. The public state
retains the S# reflection objects and spans only for the pipeline-creation call. Any required native
UTF-8 conversion is cold pipeline work; it does not create a public UTF-8 arena or stable-frame
allocation.
^rhi-core-008

### RHI-CORE-009 — Pipeline descriptions and immutable identity

The public creation descriptions are `GraphicsPipelineDesc`, `ComputePipelineDesc`,
`MeshPipelineDesc`, `RayTracingPipelineDesc` and `WorkGraphPipelineDesc`. They are cold-path stack-only
descriptions where they contain spans. Every shader member is the selected linked/specialized S#
program plus S# entry-point reflection; no description accepts DXIL reflection, a backend shader
object or a duplicate entry-point string.

Graphics creation consumes vertex-buffer layouts, `VertexAttribute` locations/formats/offsets,
primitive topology/strip cut, RasterizerState, MultisampleState, DepthStencilState, BlendState,
AttachmentFormatSignature, optional StreamOutputState and the declared DynamicState set. Compute
consumes one compute entry point. Mesh consumes one mesh entry point, optional amplification/pixel
entry points and the graphics output/fixed-function subset that actually affects the mesh pipeline.
Ray-tracing and Work Graph additions are defined in RHI-CAP-005 through RHI-CAP-008.

All caller spans are consumed before creation returns. The returned Pipeline is a non-generic,
immutable DeviceResource that owns normalized copies/hashes of the values required for compatibility
and cache identity; it does not retain caller stack memory. Normal creation returns the Pipeline;
Slang, root-artifact or native creation failure throws `GraphicsException` with the available
diagnostic. Binding a Pipeline is a future-state setter governed by RHI-QUE-006. A diagnostic Label
never changes compatibility or cache identity.
^rhi-core-009

### RHI-CORE-010 — Adapter, Device and Queue creation

Adapter enumeration writes `AdapterInfo` snapshots into a caller span. If the destination is too
small, the Try operation returns false, reports the required count and writes no partial array;
invalid spans/options throw normally. AdapterId is the stable opaque selection identity for that
enumeration/runtime, not a native pointer.

`DeviceDesc` fixes the chosen AdapterId, required/optional DeviceFeatures, `RetirementType`, Queue
counts/priorities and enabled node mask. A missing required feature, unavailable Queue count or native
Device creation failure throws `GraphicsException`; an absent optional feature leaves only its named
capability unavailable. The created Device owns one immutable `AdapterInfo`, `DeviceCapabilities`,
format/limit snapshot and its Queues. `GetQueue(Device, QueueType, index)` returns a borrowed Queue;
QueueType is exactly Graphics, Compute or Copy and the index must be below the created count.

Device's public states are Active, Lost and Disposed. Device removal changes Active to Lost once,
records native diagnostics and makes all native behavior throw `GraphicsException(DeviceLost)`;
Dispose changes Active/Lost to Disposed and follows RHI-LIFE-002/RHI-LIFE-003. Immutable
Adapter/Capabilities/provenance metadata remains readable after Lost/Dispose, but Queue/native
operations do not. Surface is created by the backend runtime for a platform window and remains
backend-scoped; common Device/resource assemblies do not depend on a window-system package.
^rhi-core-010
