# RHI-D3D12-007 — Capability availability and native calls 

Typed capabilities are constructed only when the selected adapter/runtime exposes every native fact
required by their public operations. Examples include:

- sparse-resource tier, format and tile information;
- sampler-feedback tier and descriptor/resolve support;
- residency interfaces and DXGI budgets;
- DXR tier, state-object and command-list interfaces;
- mesh/VRS tiers and limits;
- released Work Graph tier and properties;
- ExecuteIndirect argument support;
- linked-node masks and external-handle operations;
- presentation, calibrated timestamps and Pipeline creation support.

Native creation or command failure remains authoritative even after positive feature discovery.
Unreleased or unbound native features are not advertised by inventing a portable substitute.
^rhi-d3d12-007

