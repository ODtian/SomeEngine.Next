# RHI-CACHE-004 — Asynchronous creation and warm-up boundary 

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

D3D12 keeps accepted requests on the Device and executes compilation through the common Job scheduler.
The Device queue, shared category and closing order are described in
[D3D12 asynchronous creation](../Backends/D3D12/Production-allocation-asynchronous-creation-and-diagnostics.md). Its
`D3D12Diagnostics.PipelineCreation` snapshot reports accepted/queued/running/ready/failed/Device-Lost
counts, cache lookups, Queue wait, native creation duration and family counts.

Renderer/Runtime policy sits above this primitive: request deduplication, material/pass manifests,
priority, load-phase waiting, fallback rendering and unused-prewarm accounting are not RHI concepts.
No frame should perform Slang compile/link/codegen or first-use PSO creation.
^rhi-cache-004
