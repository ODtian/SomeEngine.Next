# RHI-CORE-001 — Portable execution boundary 

SomeEngine RHI is a backend-neutral execution API with explicit resources, views, Queues,
synchronization, descriptors, Pipelines, presentation and typed modern-GPU capabilities. Direct3D 12
is the first production backend. The strict conformance backend is an executable proof that the core
contract is not defined by D3D12 object shapes.

Backend-specific native access is opt-in and typed. Portable objects never expose D3D12 private
derived types as their public identity.
^rhi-core-001

