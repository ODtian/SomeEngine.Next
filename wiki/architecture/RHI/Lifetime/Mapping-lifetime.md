# RHI-LIFE-006 — Mapping lifetime 

`Map` returns a `MappedBuffer` value tied to one internal mapping sequence and an absolute Buffer
range. Copies of that value share the same terminal sequence. `Flush` and `Invalidate` accept only
absolute subranges contained by the mapped window. A zero-length range is valid only at a contained
boundary.

Dispose ends the shared sequence exactly once and performs the native Unmap contract. A stale copy
cannot operate after the sequence ends. The D3D12 backend rejects a second active mapping of the same
resource where its native mapping policy requires exclusivity.
^rhi-life-006

