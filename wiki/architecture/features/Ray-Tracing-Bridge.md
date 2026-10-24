# Ray Tracing Bridge

**状态：实现已于 2026-10-04 删除（#88）。** `StreamOutputFeature`、`RayTracingGeometryFeature` 及共享几何导出实现的引用核对见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## 历史设计

Native ray tracing 是 geometry/material 能力的 consumer 或独立 feature，不因消费 Cluster 数据而成为 Cluster core。

其 acceleration structure、shader table 与 dispatch 仍使用普通 RHI/RenderGraph 能力。
