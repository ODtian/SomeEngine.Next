# RHI Pipeline Cache

### RHI-CACHE-001 — Versioned deterministic envelope

`PipelineCache` persists one versioned RHI envelope rather than exposing a raw backend blob. Sections
are keyed by normalized pipeline identity, backend/family tag and compatibility digest. Serialization
and merge are deterministic: source order cannot change the resulting bytes, and corruption fails
closed. The real product codec defines the byte layout once it is implemented and its normal tests own
golden vectors. The plan does not maintain a speculative reference codec or a separate wire schema.
^rhi-cache-001

### RHI-CACHE-002 — Compatibility-local misses

Adapter, driver, Agility, Slang, root-layout or schema incompatibility invalidates only the affected
native section. It does not discard normalized keys or unrelated backend/family sections. Unknown
well-formed section tags are preserved so deterministic merge does not destroy another backend's
data; unknown envelope schema or corrupt ranges/hashes are rejected.
^rhi-cache-002

### RHI-CACHE-003 — Pipeline-family coverage

The envelope covers graphics, compute, mesh, ray tracing and work-graph identities. D3D12 classic
PSOs may use CachedPSO/PipelineLibrary. State-object families use a native state-object database when
supported; otherwise the same exact normalized key drives an in-memory cache and replay/warm
manifest. Public cache semantics never claim that all families are `ID3D12PipelineLibrary1` entries.

Every family key begins with stable identities for the exact linked/specialized Slang program and
emitted shader bytes, target/profile/capability/compiler options, global/local root-layout signatures,
pipeline family and node mask. It then contains exactly these public inputs:

| Family | Family-specific normalized key input |
|---|---|
| Graphics | Vertex attributes/buffer layouts, primitive topology and strip cut, rasterizer, multisample/sample mask, depth/stencil, blend, attachment-format signature, stream-output elements/gaps/strides/rasterized stream and declared dynamic-state set. |
| Compute | Compute entry point and its specialization identity; no graphics-state defaults are inserted. |
| Mesh | Mesh, optional amplification and optional pixel entry points plus the same attachment/raster/depth/blend/multisample/dynamic inputs that affect its native pipeline. |
| Ray tracing | Ray-generation/miss/callable entry points, hit-group exports and their S# entry points, global/local layouts, recursion depth, payload/attribute sizes, pipeline flags and state-object additions. Shader-table records and their bound resources are runtime data and do not enter the pipeline key. |
| Work Graph | ProgramName, reflected node/entry identities and overrides, global/local layouts, program flags and declared maximum input-record configuration. Backing Buffer address/content and dispatch records are runtime data and do not enter the key. |

Labels, object addresses, cache objects and Slang session-local target indices never enter a
persistent key. Enums use their permanent product numeric value, integers use fixed-width little
endian encoding, sequences include count then elements in public order, and floating values use the
same canonical equality as
[[Queue-and-Commands#RHI-QUE-006 — Exact state-suppression policy]] (`+0 == -0`; all NaNs use one
canonical encoding). Stable S#,
root-layout and attachment signatures are content hashes, not process object identity.

Compatibility identity includes adapter/driver identity, Agility and Slang versions, backend ABI,
root-layout compiler version, key/envelope schema versions and the immutable Device capability
snapshot. Exact envelope section tags, offsets, checksum algorithm and on-disk packing remain private
choices of the one product codec and its golden tests; they are not a second pre-implementation wire
format.
^rhi-cache-003
