# Ray Tracing Bridge

Native ray tracing 是 geometry/material 能力的 consumer 或独立 feature，不因消费 Cluster 数据而成为 Cluster core。

其 acceleration structure、shader table 与 dispatch 仍使用普通 RHI/RenderGraph 能力。

`DefaultGameRayShadowTests` 使用 GPU inline `RayQuery` 为 1024 小球、Sponza 和 Amazon Bistro
生成阴影真值。参考从源 glTF 构建 BLAS/TLAS，保留源几何、实例变换与材质透明度遮罩。
相机和光源从同次渲染的场景资产读取。测试将材质输出的 VSM 可见性与 RT 遮挡结果逐像素比较，
保留场景图、两种阴影图和差异数据。该入口随普通测试发现与执行。
