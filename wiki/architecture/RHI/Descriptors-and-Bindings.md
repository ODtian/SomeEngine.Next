# RHI Descriptors and Bindings

### RHI-DSC-001 — Bindless identity and mutable tables

A Bindless view is the same immutable ordinary view identity plus one stable logical
`DescriptorIndex`; it directly derives from its ordinary view type and can be used wherever that
ordinary view is accepted. It has no object-level publication flag and cannot be rewritten from an
arbitrary other view. Mutable stable-index content is represented separately by `DescriptorTable`,
whose `uint` slots are pending descriptor storage rather than view identities. Ordinary view creation
does not allocate a bindless index. The acceleration-structure pair follows the same rule explicitly:
`AccelerationStructureSrv` is the ordinary descriptor identity and
`BindlessAccelerationStructureSrv : AccelerationStructureSrv` adds only the stable index; neither one
is the acceleration-structure storage identity.

The complete shader-visible inheritance set is `BindlessBufferCbv : BufferCbv`,
`BindlessBufferSrv : BufferSrv`, `BindlessBufferUav : BufferUav`,
`BindlessTextureSrv : TextureSrv`, `BindlessTextureUav : TextureUav`,
`BindlessSampler : Sampler` and the acceleration-structure pair above. Each Bindless abstract base
initializes one concrete, nonvirtual, get-only `uint DescriptorIndex` in its base constructor. There
is no index interface, virtual getter, delegate or separate index-state object. RTV and DSV views are
not shader-visible and therefore have no Bindless form.

Specification correction: the earlier count-only
`CreateDescriptorTable(Device, DescriptorTableType, uint count)` shape could not determine the native
kind of an initial Resource null descriptor and therefore could not satisfy its own Type-correct-null
requirement. The terminal API is
`CreateDescriptorTable(Device, ReadOnlySpan<ResourceBindingType> slotTypes)`. The nonempty span fixes
each slot's immutable descriptor Type; `None` is not a table slot Type, and Resource and Sampler Types
cannot be mixed in one table. `DescriptorTable.Type` is derived from that span, while `Count` is the
span length. Slot numbers are zero-based, and `GetDescriptorIndex(table, slot)` returns the stable
global `uint` index used by shaders.

Each Resource slot accepts only the BufferCbv/Srv/Uav, TextureSrv/Uav or
AccelerationStructureSrv Type declared for that slot, or a Type-correct null of that same Type. A
Sampler slot accepts Sampler or null. `WriteDescriptor(table, slot, value)` changes only pending
content and rejects a Type different from the slot declaration without changing that pending
content. All slots begin pending Type-correct null and become shader-visible only through
PublishDescriptors. Table growth is not exposed; the global heaps may grow underneath while every
table index remains stable.

If the backend replaces native storage behind the same public Resource identity, every existing View
keeps the same Description and, for a Bindless view, the same DescriptorIndex. The backend stages the
refreshed native descriptor for the next publication generation. This internal resource rename is not
permission to rewrite one public View from another View.
^rhi-dsc-001

### RHI-DSC-002 — Atomic descriptor publication

`PublishDescriptors` atomically publishes pending persistent bindings and table writes as one new
generation; already-open contexts keep their captured generation. OutOfDescriptors and recoverable
NativeFailure throw while preserving the current generation and every pending write for retry.
DeviceLost preserves the same atomic visibility boundary but makes retry impossible because Device
is terminal. No failure exposes a partial candidate generation.

A persistent parameter binding is Unpublished until its complete descriptor content has entered a
published generation, then Published. Its first Dispose immediately makes the public binding Disposed
and unusable; `DisposalRequested` and `Retired` are internal native-allocation stages, not alternate
public Dispose semantics. Dispose never alters an already published generation. If the binding was
still Unpublished, disposal atomically removes its pending candidate and releases its never-visible
physical range. If it was Published, Automatic mode retires its physical ranges and captured public
payload after every capturing context/payload/generation completes. In Manual mode the caller must
wait before Dispose, after which only internally owned native generation storage can remain pending
retirement. Publication never resurrects a disposed binding. A default or foreign-device binding is
always rejected.

A Bindless view is immediately usable as its ordinary view and exposes its immutable DescriptorIndex
at creation, but shader indexing is a caller precondition until a generation containing that descriptor
has been published. The optional Validation Layer diagnoses unpublished indexing; the direct backend
does not test publication at each binding or draw. Disposing it before first publication cancels the
pending descriptor. Public view disposal is immediate and idempotent. Automatic mode delays index
and physical-range reuse until every recording, descriptor generation and submitted use that
references the view has completed; Manual mode permits reuse only after a valid Dispose by a caller
that has already waited for every relevant completion. A stale object can
never alias a replacement index in either mode.

DescriptorTable writes always enforce slot bounds and Resource-versus-Sampler table Type because
those select and address actual descriptor storage. The Validation Layer diagnoses a foreign Device
before forwarding. Writing a resource descriptor to a Sampler table or a Sampler to a Resource table
is ArgumentException and changes no pending write. Descriptor-generation identities never wrap.
Exhausting the final identity throws GraphicsException(OutOfDescriptors), preserves the current
generation and pending writes, and permanently closes further publication on that Device; existing
generations remain usable until normal retirement and recovery requires creating a new Device.
^rhi-dsc-002

### RHI-DSC-003 — Objects retained by a descriptor generation

In Automatic retirement mode, every live descriptor generation strongly retains, for every
published slot, the descriptor/view identity and every referenced resource, counter Buffer, Sampler
or AccelerationStructure. Overwriting A with B affects only a later generation; typed null also
affects only the later generation. Public Dispose of a table, view, resource or sampler is immediate;
only destruction or reuse of its native descriptor/storage, logical index and physical range waits
until every generation that references it retires.

In Manual retirement mode, the RHI retains each immutable native descriptor-heap generation and its
physical ranges for as long as a captured Context or submitted payload can address it, but it does not
strongly retain the public slot payload. The caller keeps every referenced view/resource/sampler and
its native storage alive through the relevant Queue completions before disposing or reusing it.

DescriptorTable.Dispose immediately ends future Write/Publish use and discards never-published pending
writes; it does not Dispose objects named by its slots. Already-published generations captured by a
Context or submitted payload remain immutable. In Automatic mode they strongly retain each slot's
view and referenced resource/counter/Sampler/AccelerationStructure; in Manual mode they retain only
the internally owned native generation. Dispose never
publishes pending writes or rewrites a published generation.
^rhi-dsc-003

### RHI-DSC-004 — Descriptor heap growth

Logical resource and sampler indices remain stable across runtime growth. Publication prepares the
candidate heap generation, copies live descriptors and applies pending writes before it becomes
current. Old contexts continue using the old native generation until all Queue watermarks retire it.
Growth is a cold operation; steady command encoding never allocates an array or object to materialize
descriptor descriptions.
^rhi-dsc-004

### RHI-DSC-005 — Slang-derived binding

S# `VariableLayoutReflection` and its parameter layout are the binding authority. Persistent bindings
materialize versioned content that can be reused and content-compared; each published version is
immutable, while a later Update stages the next version. Transient bindings are
caller-owned stack/span packets. The base RHI reads that layout only to materialize descriptors and
root arguments. The Validation Layer separately diagnoses incompatible parameter blocks, missing or
duplicate fields and wrong resource usage; no equivalent reflection walk remains in the direct hot
path. Persistent content is materialized at creation or descriptor publication and binds without
per-frame reconstruction; transient content is copied into the recording context's pre-sized
descriptor arena and ordinary-data storage. Equal normalized content may reuse already-materialized
descriptors and suppress a redundant root/table setter, but no binding call changes barrier state or
resource lifetime implicitly. D3D12 may cache native descriptor/root binding state, but it does not
introduce a second shader binding namespace, layout version or semantic table.
^rhi-dsc-005

### RHI-DSC-006 — Ordinary views and permitted formats

Resources and views are separate reference identities. The ordinary view families are `BufferCbv`,
`BufferSrv`, `BufferUav`, `TextureSrv`, `TextureUav`, `ColorAttachmentView`, `DepthStencilView`,
`AccelerationStructureSrv` and `Sampler`. A Buffer/Texture does not synthesize all possible views at
creation, and a ColorAttachmentView is not a shader-resource view with another flag. Each view has
one immutable Description and one Resource (Sampler has only its Description); backend private
children contain the native descriptor identity.

Resource creation fixes usages and, for a Texture format family that needs them, the exact permitted
view formats. View creation cannot add an undeclared usage, reinterpret to an undeclared format,
escape the resource/subresource/byte range or violate alignment. Those are caller contract errors and
throw `ArgumentException` before native descriptor creation; they are not an ordinary
`IncompatibleFormat` status. D3D12 descriptor creation has no HRESULT, so every field needed to avoid
an invalid native descriptor is checked in the base path. A rare failure from allocating/copying its
descriptor storage follows the normal `GraphicsException` policy.
^rhi-dsc-006

### RHI-DSC-007 — Parameter binding inputs

`ResourceBindingType` has exactly None, ConstantBuffer, BufferSrv, BufferUav, TextureSrv, TextureUav,
Sampler and AccelerationStructure. One `ResourceBinding` contains that Type and the corresponding
ordinary view/Sampler plus any required array element; it does not contain a backend slot number.
`ParameterBlockBindings` is a stack-only operation description over caller-owned spans of those
values and ordinary scalar data. The spans are consumed synchronously.

A sampler carrying S# `ImmutableSamplerReflection` is shader-owned immutable state, not a runtime
`ResourceBinding`: S# retains its complete binding range and typed state but omits it from the
canonical bounded `BindingElements` sequence. Consequently the caller span contains no element for
that sampler. A sampler without that S# fact remains an ordinary runtime Sampler binding.

`PersistentParameterBindings` materializes one complete parameter block for repeated use. Create or
Update stages a complete replacement; there is no partially updated published version. Each call
uses its S# `VariableLayoutReflection` to materialize the required fields once, then descriptor
publication makes the new materialized content visible under
[[Descriptors-and-Bindings#RHI-DSC-002 — Atomic descriptor publication]]. Transient binding copies
the supplied descriptor/scalar content into the current Context's pre-reserved storage. Neither form
changes resource synchronization state. Content equality includes each binding Type, view/Sampler
semantic equality, array position and scalar bytes; it never compares caller span addresses.
^rhi-dsc-007
