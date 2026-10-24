# RHI-D3D12-001 — Fixed native boundary 

`D3D12Backend` is a sealed `IGraphicsBackend` implementation. Public abstract resources are converted
only after checking backend owner, expected private D3D12 derived type and, where relevant, exact
Device. Managed object identity is never proven with `Unsafe.As`.

The backend uses the pinned Agility SDK and Silk.NET native declarations. Public enum members and
operations have explicit D3D12 mapping branches; an unknown value is rejected rather than falling
through to a guessed native constant.

Slang target code, reflection and native D3D12 descriptions live only at the creation/encoding
boundary. Backend-private root slots, descriptor offsets and native leases do not escape as portable
state.
^rhi-d3d12-001

