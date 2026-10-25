# LOD DAG

Cluster 的 LOD 关系保持 DAG。简化生成的父级 cluster 与基础 cluster 一起属于完整 cluster 集合。

LOD 关系不替代 spatial hierarchy；它只表达几何细节层级与 selection 所需关系。

参见 [[Spatial-BVH]]。
## 同层组构建

本节记录 `ClusterMeshBuilder.BuildLod` 已实现的 CPU 构建行为，代码位于 `SomeEngine.Geometry.Cluster/ClusterMeshBuilder.cs`。资产导入的 `ClusterBuilder.BuildGeometry` 与程序几何的 Offline 构建共同调用这一入口。

每层先通过既有 Partition 与 LockBoundary 确定分组和边界锁，再将各组交给 `JobSystem.ExecuteParallel`。每组持有自己的合并索引、简化输出、父 meshlet 与父索引列表，并沿既有 Simplify、Clusterize 和包围计算生成结果。构建调用为来源锁复制输入；默认入口把租用锁缓冲的有效范围初始化为零，再形成每层边界锁。输入位置与锁在同步批次范围中固定，批次返回点覆盖全部读取完成。

构建调用在批次返回后按原组顺序分配父组编号，追加父索引并修正 meshlet 的全局索引偏移，同时更新子 cluster 的 ParentGroup、ParentError 和 LOD 球。下一层随后读取完整的 cluster 与索引集合。材料归类、简化阈值、误差计算和根处理沿用同一条构建链。

公开行为入口见 `ClusterLodLevelTests` 的层级和交汇误差检查、`ClusterGeometryImportTests.LodKeepsSeamVerticesAndTriangleMaterials` 的来源边界与材料检查，以及 `ProceduralClusterRenderingTests.ParallelLodBuildDisplaysTheSameSurfaceAsOneExecutionThread` 对相同细分网格在单执行线程与多个 worker 下构建后呈现的逐像素比较。
