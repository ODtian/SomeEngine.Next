# Spatial BVH

Spatial BVH 将所有 LOD clusters 都作为 leaves，再在这些 leaves 之上建立空间层级。

内部节点只负责空间 culling；leaf 对应具体 cluster candidate。LOD DAG 与 spatial BVH 是正交结构。

参见 [[LOD-DAG]] · [[architecture/shader/Conservative-Bounds]]。
## CPU 构建与消费

本节记录已实现的构建职责，入口为 `ClusterMeshBuilder.Build` 和 `ClusterGeometryBlock.OpenPayloadSourceAsync`；实现分别位于 `ClusterMeshBuilder.cs` 与 `Assets/Pipeline/ClusterMeshData.cs`。

构建方先按页、归约组与父组确定叶范围，再计算每层节点数和连续数组区段。叶节点在各自区段汇总 cluster 包围范围；同层父节点在各自区段汇总连续子节点的 AABB、LOD 球和误差。各批通过 `JobSystem.ExecuteParallel` 执行，返回点覆盖整批完成，下一层由此读取完整的子结果。构建方持有输入和节点数组，直到最后一层完成并序列化。

叶节点按原页顺序排列，内部节点逐层排列，根位于末尾。程序几何将数组写入 Mesh 的 BVH 字节范围；资产页组合将数组交付给 Mesh 与页来源。Rendering world 的驻留安装沿现有入口读取这些字节，GPU traversal 使用节点子范围完成空间剔除；顶点与属性字节继续由材质解释。

公开行为检查见 `ClusterMeshBuilderTests.BvhBoundsCoverDistributedGeometry`、`ClusterBuilderTests.AssetBvhBoundsCoverDistributedGeometry`，应用呈现沿 `ProceduralClusterRenderingTests.RealtimeAndOfflineGeometryDisplayTheSameMaterialRegions` 消费同一 BVH 表示。
