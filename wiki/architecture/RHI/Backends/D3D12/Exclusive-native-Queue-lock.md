# RHI-D3D12-005 — Exclusive native Queue lock 

Exclusive Queue-native access is represented by a stack-bound lock/lease sequence. Acquiring it
serializes against RHI submission and presentation on the same Queue. Dispose releases exactly that
sequence; copied or stale values cannot release another acquisition.

The lock does not create a second Queue, change completion numbering or transfer native Queue
ownership. Device teardown waits for the same synchronization authority before releasing Queue state.
^rhi-d3d12-005

