# Direct3D12 / Vortice backend

The Direct3D12 backend is native-only. Constructing `SomeEngine.Graphics.Direct3D12.Device` creates a real Vortice/ID3D12Device, graphics/compute/copy queues, fences, descriptor arenas and native runtime tables. Unsupported hosts fail during construction with `PlatformNotSupportedException` instead of entering a software authoring mode.

There is no Direct3D12-specific Null oracle, no delegated validation backend, no headless Direct3D12 mode, and no optional backend fallback path. Cross-platform contract tests belong to `Graphics.Null`; Direct3D12 tests that require native objects are Windows-only.

The backend owns:

- Graphics handle allocation and generation state.
- ID3D12Heap / ID3D12Resource / descriptor / root-signature / PSO / query / command allocator / command list tables.
- Mesh, DXR and Work Graph artifact construction.
- Native queue submission through the D3D12 context queues.

Unsupported D3D12 capabilities must surface as explicit capability failure or API construction failure. They must not silently downgrade to a software lane inside the Direct3D12 backend.
