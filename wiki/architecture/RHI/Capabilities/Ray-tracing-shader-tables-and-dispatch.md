# RHI-CAP-006 — Ray-tracing shader tables and dispatch 

A `RayTracingShaderTable` belongs to one Ray Tracing Pipeline. Table capacity and record categories are
fixed at creation. An update supplies exact reflected entry/hit-group identities, local parameter
blocks and ordinary data. The backend validates record alignment/stride and materializes native shader
identifiers privately.

`RayTracing.ShaderRecordResourceBindings` distinguishes the DXR local-root ABI from Vulkan shader
records. DXR may materialize opaque resource and sampler bindings in each record. Vulkan records carry
ordinary bytes only; Vulkan shaders address opaque resources through global bindless descriptor indices
stored in those bytes.

`DispatchRays` names a validated table, ray-generation record and direct dimensions. Indirect dispatch
uses explicit argument storage and capability support. Shader-table updates and dispatch do not add
barriers. The table, Pipeline, captured descriptor/native generations and record resources remain
physically retained through completion.
^rhi-cap-006

