# RHI-CAP-005 — Ray-tracing Pipelines and acceleration structures 

`RayTracing` reports tier, Pipeline/inline support, limits, alignments and optional operations.
Acceleration structures are storage resources with explicit type and size. Build/update input names
every geometry/instance Buffer range, destination and scratch range. Build information is queried for
the exact description before recording.

`AccelerationStructure.DeviceAddress` supplies the GPU address used in an instance's
geometry reference. D3D12 and Vulkan expose their native addresses through this data;
the structure and its backing storage retain their ordinary RHI lifetimes.

Build, copy, compaction query, serialization and post-build information are explicit commands. No
operation allocates a hidden destination, inserts a barrier or substitutes a rebuild. Ray-tracing
Pipeline creation consumes the linked S# program, reflected exports/hit groups, exact limits and
static samplers. It is available synchronously and asynchronously through `IGraphicsBackend`.
^rhi-cap-005

