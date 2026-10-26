# RHI-D3D12-008 — Production allocation, asynchronous creation and diagnostics 

The private resource allocator uses 64 MiB Device-local blocks and 16 MiB upload/readback blocks,
partitioned by memory type, heap class and node masks. Eligible small resources use placed
suballocation; large or unsuitable resources fall back to committed allocation. Device-local block
growth checks DXGI budget pressure. Suballocations return only when their final `NativeLease`
releases, and each pool keeps at most one warm empty block.

Allocator construction and growth are failure-atomic: managed containers are reserved before native
Heap creation, native pointers remain in local cleanup scopes until ownership transfer, and failed
placed-resource construction returns both COM and suballocation state.

Each Device owns a bounded Pipeline creation queue with at most 256 requests and one to four dispatched
requests, using the existing processor-count budget. Acceptance captures `JobSystem.Current`; each
dispatched request uses that Scheduler's `D3D12 pipeline` category through `ScheduleAsync`. The category
allows 256 in-flight Jobs, at most four executing workers within the Scheduler's worker count, and a
service share of one. The Device keeps requests in its queue until the concrete Job starts, including
requests waiting for the shared category. `JobHandle.CompleteAsync` delivers the complete Job result,
using the coordinating caller to execute a zero-worker Scheduler. Its dispatched budget returns after completion.

Request packets own all span data, Slang global/session/program references and cache use. The concrete
Job performs the original native creation, releases captured inputs, and delivers the original
`Task<Pipeline>`. Cache lookup context is saved and restored around that execution. Device teardown
stops acceptance, faults unstarted requests through their queue nodes, and waits for running requests
to finish cleanup and deliver their Task result before releasing the native Device. The existing Task
remains the single Pipeline result for callers.

Implementation: [request capture and Device queue](../../../../../src/SomeEngine.Graphics.Direct3D12/PipelineCreation.cs)
and [shared Job execution](../../../../../src/SomeEngine.Graphics.Direct3D12/PipelineCreation.Jobs.cs).
Public checks in `WarpCapabilityTests` cover asynchronous cache prewarm, retained Shader inputs with
actual GPU output, and closing a Device with an accepted request waiting for the shared worker.

`D3D12Diagnostics` is the single diagnostics surface. It exposes debug/DRED configuration, allocator
telemetry, asynchronous Pipeline telemetry, retained Device Lost data and per-Swapchain presentation
telemetry. DRED reports are immutable, preserve partial breadcrumb/page-fault success and mark every
bounded-chain truncation explicitly.
^rhi-d3d12-008
