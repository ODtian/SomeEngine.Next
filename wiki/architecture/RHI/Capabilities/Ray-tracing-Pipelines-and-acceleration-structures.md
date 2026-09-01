# RHI-CAP-005 — Ray-tracing Pipelines and acceleration structures 

`RayTracing` reports tier, Pipeline/inline support, limits, alignments and optional operations.
Acceleration structures are storage resources with explicit type and size. Build/update input names
every geometry/instance Buffer range, destination and scratch range. Build information is queried for
the exact description before recording.

Build, copy, compaction query, serialization and post-build information are explicit commands. No
operation allocates a hidden destination, inserts a barrier or substitutes a rebuild. Ray-tracing
Pipeline creation consumes the linked S# program, reflected exports/hit groups, exact limits and
static samplers. It is available synchronously and asynchronously through `IGraphicsBackend`.
^rhi-cap-005

