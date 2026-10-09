# Ray Tracing Bridge

**状态：实现已于 2026-10-04 删除（#88）。** `StreamOutputFeature`、`RayTracingGeometryFeature` 及共享几何导出实现的引用核对见 [[history/Rendering/Removed-Code-2026-10-04|删除记录]]。

## 历史设计

Native ray tracing 是 geometry/material 能力的 consumer 或独立 feature，不因消费 Cluster 数据而成为 Cluster core。

其 acceleration structure、shader table 与 dispatch 仍使用普通 RHI/RenderGraph 能力。

## 硬件阴影回归

`DefaultGameRayShadowTests` 使用 GPU inline `RayQuery` 为 1024 小球、Sponza 和 Amazon Bistro
生成阴影真值。参考从源 glTF 构建 BLAS/TLAS，保留源几何、实例变换与材质透明度遮罩。
相机和光源从同次渲染的场景资产读取。测试将材质输出的 VSM 可见性与 RT 遮挡结果逐像素比较，
保留场景图、两种阴影图和差异数据。该入口归入 Hardware 类别，通过硬件测试入口执行。
