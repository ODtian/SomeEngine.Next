# RHI-CORE-009 — Pipeline descriptions and identity

Graphics, Compute, Mesh, Ray Tracing and Work Graph pipeline descriptions contain cooked backend shader code, Slang# detached reflection/layout, selected entry metadata and the required fixed-function or capability-specific state.

Labels只用于 diagnostics，不进入 compatibility 或 cache key。

同步和异步 pipeline creation 产生相同设备结果；异步创建必须在返回前接管所有 span-backed description data 与所需 asset lifetime。成功完成的 pipeline 已可直接绑定，不允许 first-use shader compilation/link/reflection。

参见 [[architecture/shader/Shader-Build]] · [[architecture/shader/Detached-Reflection]]。
^rhi-core-009
