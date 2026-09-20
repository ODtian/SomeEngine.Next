# RHI-IMPL-003 — Direct3D 12 mapping coverage 

Every public resource usage, view shape, format feature, Queue type, barrier scope/access/layout,
Pipeline state, query type, presentation status and typed capability has an explicit D3D12 mapping or
an explicit unsupported branch.

Native void-return descriptor methods are preceded by complete legality validation. HRESULT-returning
operations preserve the native code and query Device removal where required. Capability discovery is
not treated as proof that a later native creation cannot fail.
^rhi-impl-003

