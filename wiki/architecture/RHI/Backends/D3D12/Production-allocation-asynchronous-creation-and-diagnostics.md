# RHI-D3D12-008 — Production allocation, asynchronous creation and diagnostics 

The private resource allocator uses 64 MiB Device-local blocks and 16 MiB upload/readback blocks,
partitioned by memory type, heap class and node masks. Eligible small resources use placed
suballocation; large or unsuitable resources fall back to committed allocation. Device-local block
growth checks DXGI budget pressure. Suballocations return only when their final `NativeLease`
releases, and each pool keeps at most one warm empty block.

Allocator construction and growth are failure-atomic: managed containers are reserved before native
Heap creation, native pointers remain in local cleanup scopes until ownership transfer, and failed
placed-resource construction returns both COM and suballocation state.

Each Device owns a bounded Pipeline creation queue with at most 256 requests and one to four worker
threads. Request packets own all span data, Slang global/session/program references and cache use.
Device teardown stops acceptance, faults unstarted work, joins workers and only then releases the
native Device.

`D3D12Diagnostics` is the single diagnostics surface. It exposes debug/DRED configuration, allocator
telemetry, asynchronous Pipeline telemetry, retained Device Lost data and per-Swapchain presentation
telemetry. DRED reports are immutable, preserve partial breadcrumb/page-fault success and mark every
bounded-chain truncation explicitly.
^rhi-d3d12-008
