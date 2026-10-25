# Deformed Bounds

本文记录 LBS 影响包围体、实例边界输出和 Cluster 的共同消费接口。
源码入口为 [ClusterSkinBoundsImporter](../../../src/SomeEngine.Assets.Importers/Importers/ClusterSkinBoundsImporter.cs)、
[SkinBoundsFeature](../../../src/SomeEngine.Render.Features/Skinning/SkinBoundsFeature.cs)、
[IClusterBoundsSource](../../../src/SomeEngine.Render.Cluster.Pipeline/IClusterBoundsSource.cs)
和 [cluster_deform_bounds.slang](../../../assets/Shaders/cluster_deform_bounds.slang)。

## 导入数据与网格对应

Cluster 导入集合为带来源影响数据的网格生成 `ClusterSkinBoundsBlock`。
每个几何 cluster 按来源 joint 归集所有正权重顶点，包围盒包含实际解码后的量化位置。
同一记录保存简化 group、parent group、level、几何误差和位置量化步长。
`CreateBounds()` 返回同步读取的 `ClusterSkinBounds`，普通引用保持这些数组的使用期。

盒子位于网格模型空间。配套 LBS 计算用 `InverseBind[joint] * G[joint]` 变换这些盒子，
再求并集。非负且归一化的 LBS 权重使加权顶点位于这个并集的轴对齐包围盒中。

几何装配在拆分材质范围和分页后保存 `Mesh.ClusterGeometryIndices`，按最终 draw cluster
顺序指向来源几何 cluster。页头 `MeshPageHeader.FirstCluster` 给出本页在该顺序中的起点。
BVH 保持共享拓扑，实例侧的盒子范围采用同一局部节点顺序。

## 应用和提取

`MeshSkinBounds` 保存已就绪的影响盒。普通 Runtime 在加载网格时取得可选的
`ClusterSkinBoundsBlock`，将使用对象放入 Game world；`SkinBoundsRenderExtractor`
沿普通提取路径复制该对象引用。

以下是显式加载时的公共 API 示例：

```csharp
ClusterSkinBoundsBlock block = await meshAsset.GetBlockAsync<ClusterSkinBoundsBlock>();
world.Add(meshEntity, new MeshSkinBounds(block.CreateBounds()));
world.Add(meshEntity, new MeshSkin(skeletonInstance, skinBinding));
```

`SkinBoundsFeature` 在装配时解析每个节点和 draw cluster 对应的影响盒，按共享几何复用定义。
实例保存自己的骨骼绑定、输入标记和输出范围。定义变化上传工作表；姿势输入变化更新对应实例的值。
静态定义读取按查询成员版本和几何、绑定、影响盒组件的 chunk 版本推进，
普通帧从已有实例更新动态姿势输入。
边界更新直接读取同一 `SkinningFeature` section 已持有的启动编号、输入编号、姿势版本和 GPU 标志，
各网格继续在自己的 `MeshBounds.Inputs` 中比较前后输入。
当前和上一姿势分别读取 `SkinningFeature` 提供的两份 G。实际提交后接受输入标记，重复输入保留结果。

`RenderDeformBounds` 在范围位置变化时发布；其 `First/NodeCount` 对应 GPU 输出位置。
CPU 几何按模型姿势版本观察，GPU 几何按输入编号观察；逐帧历史更新推进边界内容版本。
`SkinBoundsFeature` 在同一实例持有最近内容版本，`HasChangedSince` 提供相对调用方已接受版本的判断。
场景 refit 观察来源的 `Revision`；阴影通过 `CollectDeformedCasters` 将内容变化映射为当前实例行，
复用原有当前／上一世界包围体使缓存页失效，并在成功提交后接受来源版本。
逐帧姿势更新沿边界内容版本传播，实例属性继续持有其范围位置。
`ClusterInstanceSystem` 将整包 `RenderDeformBounds` 行版本都已变化的情况交给现有整包写入入口，
局部变化继续按原有行版本选择对应范围。

## Cluster 共同结果

`IClusterBoundsSource` 提供 `CurrentBounds`、`PreviousBounds`、`CompletePass` 和 `Revision`。
每个结果占三个 `float4`：最小 XYZ 与 LOD 误差、最大 XYZ、LOD 中心 XYZ 与半径。
`RenderDeformBounds(First, NodeCount)`
定位实例范围：先排列网格的 BVH 节点，再排列页顺序下的 draw clusters。节点数为零时使用网格边界。

`ClusterRenderFeature.GeometryLayout` 持有这一实例属性，普通实例存储负责传输。
Cluster 的读取只依赖共同包围体和节点对应关系。LBS 的权重、关节和盒子变换由具体 feature 持有。

骨骼完成点先于关节变换预计算。`TransformSkinJoints` 为变化实例的每个绑定关节计算
当前／上一 `InverseBind * G` 和线性伸长上界，保存在 `SkinBoundsFeature` 的持久缓冲中。
每个关节保存两组 XYZ 仿射列及伸长值，共 112 字节；盒子变换直接消费这些列。
`BuildGeometry` 为同一影响盒数组分配一个范围编号，剔除项与 LOD 项保存各自的范围编号。
共享定义同时记录每个范围用于哪些输出项的剔除盒及 LOD 球。
`TransformSkinBounds` 为变化实例的每个范围计算一次当前／上一盒子并集和伸长上界，
四个相邻 lane 分担盒子循环、归并结果，并分担这个范围的输出项。
布局重建时在同一实例内按影响盒数量排列范围工作，使同一 wave 内的范围具有相近循环长度，
同时继续相邻读取该实例的关节矩阵。静态盒保存预计算中心、半尺寸和关节编号，每项 32 字节。
剔除范围写最小／最大 XYZ；LOD 范围写选择球和各输出项的误差乘伸长上界。
两种用途写入原有 48 字节结果的独立字，不同范围各自保持一个写入者。
同一范围同时提供剔除与 LOD 时，合并写入最小值与误差。
内部 BVH 节点继续在局部空间合并影响盒，再进行逐范围的仿射变换。
多个 cluster、BVH leaf 和剔除／LOD 项共同引用同一范围时，共享这次姿势求值。
边界完成点先于场景边界、根节点筛选、层级遍历和叶级剔除。
场景 pass 按边界版本更新实例及空间组范围，既有边界消费者继续读取同一场景输出。
上一姿势的层级和 cluster 盒子配合上一实例变换用于历史深度判断。

## 姿势相关的 LOD

`SkinBoundsFeature.BuildGeometry` 为一个简化 group 合并其简化 cluster 与被替换子级的
joint 影响盒；同组叶级绘制和父级细化使用这份选择范围。内部 BVH 节点合并子节点的选择范围。
变换后的选择盒并集提供中心和半径，剔除盒继续使用该节点或 draw cluster 自身的几何范围。

简化输入在影响值发生变化的边上锁定顶点，并保留来源顶点身份，见
[ClusterBuilder.Geometry](../../../src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Geometry.cs)。
因此可简化区域共享同一加权线性变换。具体 LBS 表示以参与 joint 的最大线性伸长上界
放大几何误差，并计入简化级与基础级的位置量化步长。

[`skin_bounds.slang`](../../../assets/Shaders/skin_bounds.slang) 对每个
`InverseBind * G` 的线性部分 A 使用
`min(FrobeniusNorm(A), sqrt(OneNorm(A) * InfinityNorm(A)))`，该上界覆盖非均匀缩放形成的切变。
非负归一化权重的加权变换伸长由参与 joint 的最大上界覆盖。当前与上一姿势分别计算这些值。
共同 Cluster shader 读取结果中的完整浮点误差，层级细化与叶级 LOD 使用同一误差和选择球。

公开应用示例见 [SkinLodRenderingTests](../../../tests/SomeEngine.Runtime.Tests/SkinLodRenderingTests.cs)
与 [SkinVisibilityRenderingTests](../../../tests/SomeEngine.Runtime.Tests/SkinVisibilityRenderingTests.cs)，
分别展示近远视图的 LOD、遮挡后的再次出现，以及主视图和阴影视图消费同一姿势。

相关主题：[Skinning](Skinning.md)、[GPU Clips](GPU-Clips.md)、[Deform Cache](../cluster/Deform-Cache.md)。
