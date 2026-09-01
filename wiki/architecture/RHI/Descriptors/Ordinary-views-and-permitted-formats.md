# RHI-DSC-006 — Ordinary views and permitted formats 

Buffers and Textures are storage identities. CBV, SRV, UAV, color-attachment, depth-stencil and
acceleration-structure views are separate caller-disposed identities. A view cannot add an undeclared
resource usage, escape its byte/subresource range, use an unsupported format, violate alignment or
name a resource from another Device/backend.

D3D12 descriptor creation has no HRESULT, so every field required to prevent an invalid native
descriptor is checked before the native call. The view retains the physical resource reference needed
by its native descriptor.
^rhi-dsc-006

