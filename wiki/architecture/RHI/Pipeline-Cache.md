# RHI Pipeline Cache

### RHI-CACHE-001 — Versioned deterministic envelope

`PipelineCache` owns one in-memory RHI envelope. Its complete public input is
`PipelineCacheDesc.Data`; `TryGetPipelineCacheData` is its complete public output. The RHI does not
choose persistence paths, file locks, eviction policy or atomic replacement. Applications own those
filesystem concerns.

The envelope is versioned, deterministic and bounded by caller policy. Parsing, serialization and
merge validate magic, schema, environment data, counts, checked ranges, hashes and decoded-size
limits before committing. Cancellation is checked through variable-size work and leaves caller
output and destination cache unchanged until the final no-throw commit.
^rhi-cache-001

### RHI-CACHE-002 — Environment-local misses

Adapter, driver, Agility SDK, Slang/native compiler, backend ABI, root-signature compiler, capability
facts or schema changes make only the affected backend entry miss. Cache bytes never authorize
compatibility between live Pipeline objects.

A malformed envelope is rejected. A well-formed unknown backend section can be preserved by merge,
but is not interpreted by the current backend.
^rhi-cache-002

### RHI-CACHE-003 — Pipeline-family coverage

The cache covers Graphics, Compute, Mesh, Ray Tracing and Work Graph Pipeline families. Every key
contains exact linked/specialized Slang code identity, selected entry/export/node identity, static
sampler state, node mask, serialized native root-signature bytes and all family-specific public state.
Labels, object addresses, descriptor contents, command values and backend-private lookup indices are
excluded.

A cache hit still creates a new caller-owned Pipeline and new physical Slang/native ownership. D3D12
classic PSOs may consume cached PSO data. State-object families store and replay the exact data their
native path supports; the public contract does not pretend every family is an
`ID3D12PipelineLibrary` entry.
^rhi-cache-003

### RHI-CACHE-004 — Asynchronous creation and warm-up boundary

The same `IGraphicsBackend` exposes synchronous and asynchronous creation for Graphics, Compute, Mesh,
Ray Tracing and Work Graph Pipelines. The asynchronous methods return `Task<Pipeline>` directly; no
method-bearing compilation capability creates a second product surface.

Before returning a Task, the backend:

1. validates caller provenance and materializes every span-backed description field;
2. acquires independent Slang global-session, session and linked-program ownership;
3. retains any PipelineCache use required by the request;
4. accepts the request into a backend execution facility or throws without leaking state.

Task success means the native Pipeline/state object is fully created and immediately bindable. Native
creation failure faults that Task. Device Lost faults queued/running work through the existing Device
terminal error. The API does not expose cancellation that a driver cannot honor once native creation
has begun.

`PipelineCreationSupport` reports only optional advanced facts such as persistent cache data,
compile-required detection or specialization. Basic asynchronous creation is a core operation and is
not conditional on obtaining that capability.

D3D12 uses a per-Device bounded queue of 256 requests and one to four background workers. Its
`D3D12Diagnostics.PipelineCreation` snapshot reports accepted/queued/running/ready/failed/Device-Lost
counts, cache lookups, Queue wait, native creation duration and family counts.

Renderer/Runtime policy sits above this primitive: request deduplication, material/pass manifests,
priority, load-phase waiting, fallback rendering and unused-prewarm accounting are not RHI concepts.
No frame should perform Slang compile/link/codegen or first-use PSO creation.
^rhi-cache-004
