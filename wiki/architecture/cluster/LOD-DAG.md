# LOD DAG

Cluster 的 LOD 关系保持 DAG。简化生成的父级 cluster 与基础 cluster 一起属于完整 cluster 集合。

LOD 关系不替代 spatial hierarchy；它只表达几何细节层级与 selection 所需关系。

[`ClusterMeshBuilder.BuildLod`](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.cs) 对每组以 `SimplifyRatio` 计算目标三角形数量，执行一次带属性误差与顶点约束的简化，再按实际结果建立父 Cluster。父 Cluster 数量较子组减少且三角形数量符合 `SimplifyThreshold` 时建立该层；终止的组保留为根。顶点与三角形容量由聚类器共同满足。

每组父误差为子组累计误差的最大值加本次简化误差。子 Cluster 的父误差与父组的自身误差使用同一数值，供粗细层选择交接。

参见 [[Spatial-BVH]]。
