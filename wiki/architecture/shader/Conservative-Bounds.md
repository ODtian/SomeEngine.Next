# Conservative Bounds

## Definition
任何可能改变 geometry position 的 stage 必须为依赖它的 culling 提供足够 conservative 的有限 bounds，或在 pipeline configuration 时声明相应 culling 不可使用。

## Invariants
- 框架不猜用户算法内部位移范围。
- bounds 能力在 pipeline creation/configuration 时确定。
- 热路径不逐 item 尝试 bounds，再临时切换到 fallback。
- culling 不允许因为用户位置变化产生 false negative。

## Relations
[[User-Stage-Interfaces]] · [[architecture/cluster/LOD-DAG]]

## 材质位移范围

[`Material.BoundsExpansion`](../../../src/SomeEngine.Resources/Material.cs) 返回材质位移的保守世界空间半径，默认值为零。具体材质从自己的几何参数推导该值；参数通过普通材质属性更新。Mesh 提取把所绑定材质中的最大半径加到 `MeshInstance.BoundsExpansion`，GPU 地形准备按同一规则设置 `RenderClusterGeometry.BoundsExpansion`；该字段进入现有实例数据和场景、Cluster 裁剪。

Mesh 提取订阅材质变化，在位移半径改变时更新渲染几何的包围参数。GPU 地形准备从已经提取的材质引用取得当前半径。用户曲面的 `bounds`、`patchBounds` 和 `errorBounds` 同时给出细分所需的包围与误差；[地形材质示例](../terrain/Terrain-Materials.md) 通过同一世界空间位移参数提供两类结果。实现入口为 [MeshRenderExtractor](../../../src/SomeEngine.Render/Systems/MeshRenderExtractor.cs)、[RenderTerrainGeometry](../../../src/SomeEngine.Terrain/RenderTerrainGeometry.cs) 和 [cluster_bvh_cull_common](../../../assets/Shaders/cluster_bvh_cull_common.slang)。
