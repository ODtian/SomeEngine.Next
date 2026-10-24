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

[`NativeCommandData.WriteEndQuery`](../../../../../src/SomeEngine.Graphics.Direct3D12/NativeCommandData.cs)
retains timestamp writes in repeatable native recordings. Repeated submission captures the same query
heap's native lifetime through the command recording's existing reference set. RenderGraph checks the
actual queue, query pool and query range before reusing measured commands; its frame completion also
covers the resolve before the pool is reused. Other begin/end queries and query resolves retain their
ordinary recording path. Timestamp writes use Direct3D 12's end-only query operation, described in
[Microsoft's query reference](https://learn.microsoft.com/en-us/windows/win32/direct3d12/queries).
^rhi-d3d12-006

