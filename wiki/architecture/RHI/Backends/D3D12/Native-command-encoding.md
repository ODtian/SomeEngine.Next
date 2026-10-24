# RHI-D3D12-006 — Native command encoding 

Command methods first validate and resolve all public identities, then call concrete preparation
methods such as `PrepareCaptures`, `PrepareDescriptors`, `PrepareOrdinaryData`,
`PrepareBindingStorage`, `PrepareViewports`, `PrepareScissors` and
`PrepareRecordedRayTable`.

Only after every array, capture, descriptor range and temporary native argument has capacity does the
backend write the native command. State-shadow commit after the native call is no-throw. A warmed
recording path performs no managed allocation, reflection traversal, string lookup or LINQ.

Native command-list labels, events and object names feed PIX/debug/DRED diagnostics but never change
execution identity.
^rhi-d3d12-006

