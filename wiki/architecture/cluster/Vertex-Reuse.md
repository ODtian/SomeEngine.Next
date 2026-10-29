# Vertex Reuse

Vertex reuse batch 只覆盖一个 material range，并对 triangle 数和 unique vertex 数设置明确上限，使一个 wave 内可直接共享 vertex evaluation result。

[`ClusterMeshBuilder.BuildLocalTopology`](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Topology.cs) 在聚类容量内把每个来源顶点映射到一个局部顶点。各级 LOD 沿用聚类器输出的三角形与容量。

[`ClusterMeshPages.PackMaterials`](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshPages.cs) 按材质区段建立最多 32 三角形、32 唯一顶点的批次，优先选择与当前批次共享顶点的三角形。批次记录与重排后的三角形一起进入页数据，供光栅 binning 读取。

具体 batch 尺寸和打包允许通过 benchmark 改进；语义上不跨 material range，也不要求 hardware raster 使用同一机制。
