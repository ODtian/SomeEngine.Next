# RHI Descriptors and Bindings

### RHI-DSC-001 — Fixed DescriptorTable identity

`DescriptorTable` owns a fixed nonempty array of `DescriptorSlotDesc`. The table is either Resource or
Sampler storage, has one Device node index, and exposes a stable `Count`. There is no public slot
allocate/free API and slots are not recycled within the table lifetime.

`GetDescriptorIndex(table, slot)` returns `DescriptorIndex`. Its identity is exactly the table
reference plus numeric `Value`. Equal numeric values from different tables are not equal. The numeric
value is the value shader data may carry; the table reference remains host provenance.

`DescriptorSlotDesc` describes only the native storage shape needed by a free-standing mutable table:
resource/sampler kind, typed or structured Buffer facts, Texture view dimension/format/aspects and UAV
counter form. Pipeline-owned bounded slots are derived directly from S# and do not ask the caller to
repeat shader layout.
^rhi-dsc-001

### RHI-DSC-002 — Write and publish boundary

`WriteDescriptor` validates table ownership, slot bounds, binding type, concrete view shape and live
resource provenance before replacing CPU-side pending content. Resource slots can use the exact
supported typed null representation. Sampler slots require a concrete Sampler because D3D12 has no
null sampler descriptor.

`PublishDescriptors(Device, nodeIndex)` atomically makes all validated pending table content available
to later command recording on that node. It is an operation, not a public version object. Failure,
cancellation, descriptor exhaustion or Device Lost cannot expose a partial update. A table index does
not change when content is written or published.

Previously recorded commands retain the native descriptor generation they captured; a later publish
does not rewrite accepted work.
^rhi-dsc-002

### RHI-DSC-003 — Descriptor object retention

A table stores public binding identity in CPU staging. Command recording resolves the current native
descriptor data and captures every resource, view, Sampler, counter resource and descriptor-generation
lease required by the command.

Disposing a referenced view prevents future use, but it cannot invalidate already accepted commands.
Disposing a DescriptorTable ends future writes/index use and releases table storage after captured
native generations retire. Correct physical retention is unconditional and is not selected per
Device or per submission.
^rhi-dsc-003

### RHI-DSC-004 — Backend-private heap generations

Shader-visible heap growth, copying and generation switching are backend details. D3D12 may keep CPU
staging descriptors, allocate another shader-visible heap generation and copy stable table slots into
it before one no-throw commit.

Public code observes only stable table indices and explicit `PublishDescriptors`. Heap generation,
fragmentation, temporary descriptor ranges and completion retirement do not become public address
spaces or compatibility values.
^rhi-dsc-004

### RHI-DSC-005 — Slang-derived binding and direct native placement

The exact linked S# program and live reflection define bounded resource order, parameter-object
identity, ordinary data and static sampler declarations. D3D12 creates Pipeline-private
`NativeParameterBinding` placement during Pipeline construction. Validation independently walks the
same S# facts.

The command hot path consumes precomputed slot arrays and pure native destinations. It does not walk
reflection, parse names, calculate registers/spaces, hash strings or build a normalized layout.
Pipeline-private lookup structures are implementation details and may be changed only with measured
evidence and without introducing a public shader identity.
^rhi-dsc-005

### RHI-DSC-006 — Ordinary views and permitted formats

Buffers and Textures are storage identities. CBV, SRV, UAV, color-attachment, depth-stencil and
acceleration-structure views are separate caller-disposed identities. A view cannot add an undeclared
resource usage, escape its byte/subresource range, use an unsupported format, violate alignment or
name a resource from another Device/backend.

D3D12 descriptor creation has no HRESULT, so every field required to prevent an invalid native
descriptor is checked before the native call. The view retains the physical resource reference needed
by its native descriptor.
^rhi-dsc-006

### RHI-DSC-007 — Parameter binding inputs

`ResourceBinding` is runtime payload: a binding type plus a concrete public view/Sampler reference or
an allowed typed null. Equality uses object reference identity.

`ParameterBlockBindings` is a stack-only packet containing one exact S#
`VariableLayoutReflection`, the bounded `ResourceBinding` span in reflected order, and the exact
ordinary-data bytes including padding. It contains no register, space, root slot, route, cursor or
backend token.

`PersistentParameterBindings` is created for one Pipeline and one reflected parameter object. Update
builds a complete immutable replacement before publishing it. Independent recording contexts may bind
concurrently while another thread publishes a replacement; each bind observes and retains one exact
generation before native mutation. A recording retains a generation once even when several material
runs select it. The mutable public wrapper itself is not a second physical ownership edge. Transient
and persistent binding share the same S# validation and D3D12 placement.

`StaticSamplerBinding` pairs one S# sampler declaration identity with one `SamplerDesc`. Register,
space, array shape and stage visibility come only from S#; the state participates in Pipeline cache
identity and conflicts with runtime sampler binding are rejected.
^rhi-dsc-007
