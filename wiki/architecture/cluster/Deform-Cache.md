# Deform Cache

## Definition
每个 visible cluster 对应一段可变长度 opaque byte region；具体 Deform implementation 决定所需 bytes、alignment、写入与读取方式。

## User shader

[`IVertexDeformCache`](../../../assets/Shaders/vertex_deform_cache.slang) 扩展 `IVertexEvaluate`，用户通过关联类型 `Vertex` 定义结果，通过 `getCacheByteSize(vertexCount)` 和 `getCacheByteAlignment()` 声明字节数与对齐，通过 `writeCache` / `readCache` 解释区域。写入函数同时取得当前与上一帧的顶点结果；用户可把两次结果组织为交错、分段或其它编码。

材质把具体类型命名为 `DeformEvaluate`，以 `CachedVertexEval<DeformEvaluate>` 作为需要缓存的顶点求值类型，并引入 [`cluster_deform.slang`](../../../assets/Shaders/cluster_deform.slang) 的计算入口。直接求值路径使用同一个具体类型。自定义顶点编码通过 `CLUSTER_DEFORM_VERTEX_ENCODING` 声明入口的兼容编码标识。

默认材质模板分别通过 `RasterVertexEvaluate` 和 `VertexEvaluate` 选择光栅与着色的顶点求值类型；缓存材质在这两个入口选择相同的缓存表达。细分入口消费 `VertexEvaluate`。

`VertexEvalContext.visibleClusterIndex` 由光栅、着色和细分消费者提供。每个可见 cluster 的首个材质范围选择兼容的变形入口，同一 cluster 的材质范围共享一个区域；导入分区规则见 [Cluster Deform Compatibility](Cluster-Deform-Compatibility.md)。

## Resource use

[`ClusterPipelineOptions.MaxDeformCacheBytes`](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineOptions.cs) 设置一个帧内全部视图共享的固定字节容量，默认 64 MiB。持久图依次执行请求大小、局部前缀、组前缀、区域偏移和用户写入，随后由光栅、细分和着色读取。各阶段的资源访问由 RenderGraph 声明，缓存使用期覆盖全部消费者。

[`ClusterGeometryCapabilities`](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterPipelineOutput.cs) 的 `DeformCache` 和 `DeformRegions` 供后续图消费者声明读取。区域表使用 `uint2` 保存字节偏移及顶点数量；字节内容由用户解释。容量耗尽后，对应区域标记为溢出，`CachedVertexEval` 返回零初始化的用户顶点值。容量行为沿用 [Overflow](Overflow.md)。

[`DeformCacheRenderingTests`](../../../tests/SomeEngine.Runtime.Tests/DeformCacheRenderingTests.cs) 中的用户材质是一个验收例子：它使用带额外标记的顶点类型、32 字节对齐、区域头部空间和异或编码的标记，经应用公开入口比较缓存结果与直接变形的图像。

[Skinning](../animation/Skinning.md#inline-与缓存选择) 记录骨骼资源、每实例 `SkinBinding` 与
同一用户求值器在 inline/cache 间的选择。`ClusterDeformWrite` 的当前和上一求值分别
携带对应的实例变换与历史标志，用户缓存编码保存这两份结果。
公开应用的分阶段与整帧成本入口见 [GPU Timing](../render-graph/GPU-Timing.md)。

## Invariants
- Cluster 不解释 cache payload 字段或压缩方式。
- cache region 不要求与 vertex 数或 vertex layout 等长。
- overflow 不允许切换成 inline re-evaluate 等 correctness recovery。
- allocator 机制不属于本页语义。

## Relations
[[Cluster-Deform-Compatibility]] · [[Deform-Cache-Allocation]] · [[Overflow]]
