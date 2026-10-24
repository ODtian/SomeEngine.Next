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
当前和上一姿势分别读取 `SkinningFeature` 提供的两份 G。实际提交后接受输入标记，重复输入保留结果。

当前几何变化同时写入 `RenderDeformBounds` 的 ECS 版本，范围值保持原有对应关系。
CPU 几何按模型姿势版本观察，GPU 几何按输入编号观察；逐帧历史更新继续推进边界输入版本。
普通实例装配据此记录变化行，阴影 feature 复用既有变化行和当前／上一世界包围体使缓存页失效。

## Cluster 共同结果

`IClusterBoundsSource` 提供 `CurrentBounds`、`PreviousBounds`、`CompletePass` 和 `Revision`。
每个结果占三个 `float4`：最小 XYZ 与 LOD 误差、最大 XYZ、LOD 中心 XYZ 与半径。
`RenderDeformBounds(First, NodeCount)`
定位实例范围：先排列网格的 BVH 节点，再排列页顺序下的 draw clusters。节点数为零时使用网格边界。

`ClusterRenderFeature.GeometryLayout` 持有这一实例属性，普通实例存储负责传输。
Cluster 的读取只依赖共同包围体和节点对应关系。LBS 的权重、关节和盒子变换由具体 feature 持有。

骨骼完成点先于边界计算，边界完成点先于场景边界、根节点筛选、层级遍历和叶级剔除。
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

相关主题：[Skinning](Skinning.md)、[GPU Clips](GPU-Clips.md)、[Deform Cache](../cluster/Deform-Cache.md)。
