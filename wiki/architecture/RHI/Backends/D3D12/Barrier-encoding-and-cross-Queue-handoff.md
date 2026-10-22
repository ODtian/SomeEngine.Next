# RHI-D3D12-002 — Barrier encoding and cross-Queue handoff 

The same public barrier contract lowers to Enhanced Barriers when supported and to the legacy
resource-barrier path otherwise. `BarrierPhase.Begin`/`End` map to native split transitions; D3D12
COMMON and queue-specific layouts are selected only inside this mapper.

Memory barriers become D3D12 global barriers, while Buffer/Texture/Aliasing barriers name the exact
native resources and ranges. Queue release/acquire operations preserve caller-authored synchronization
and do not create hidden fences or waits.

Aliasing barriers activate the named resources and establish shared-memory visibility with an
ALL-to-ALL global memory barrier. RenderGraph carries that synchronization into the first access
to each reused buffer or texture. The implementation entries are
[D3D12 barrier encoding](../../../../../src/SomeEngine.Graphics.Direct3D12/CommandBarriers.cs) and
[RenderGraph synchronization](../../../../../src/SomeEngine.RenderGraph/FrameSynchronization.cs).
^rhi-d3d12-002

