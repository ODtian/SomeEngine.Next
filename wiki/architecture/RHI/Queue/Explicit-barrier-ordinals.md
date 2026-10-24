# RHI-QUE-005 — Explicit barrier ordinals 

The public barrier inventory is closed and concrete:

- `MemoryBarrier` for memory ordering without naming a resource;
- `BufferBarrier` and `TextureBarrier` for one resource/range;
- `AliasingBarrier` for before/after resource aliasing;
- `QueueRelease` and `QueueAcquire` for explicit cross-Queue ownership handoff.

`PipelineSync` names execution scope, `ResourceAccess` names real memory access and `TextureLayout`
names portable image usage. `BarrierPhase` is `Complete`, `Begin` or `End`; split state is an operation
property, not a fake Pipeline stage.

D3D12 COMMON and Queue-specific native layout choices remain inside the D3D12 mapper. Portable
`TextureLayout.General` is the backend-neutral general-purpose layout.
^rhi-que-005

