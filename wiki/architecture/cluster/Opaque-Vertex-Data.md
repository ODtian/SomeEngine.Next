# Opaque Vertex Data

## Definition
Cluster execution 不解释 vertex representation；运行时连统一 stride 都不是必要语义。

## Cluster knows
- vertex count
- opaque blob address/size
- local triangle/index data
- material ranges
- bounds/LOD/page information

## Vertex implementation knows
position/normal/UV/color 布局、量化、bit packing、压缩方式和私有 blob header。

扩展阶段的几何与着色属性分别保存在 `ClusterGeometryBlock`、`ClusterVertexAttributesBlock`，构建侧通过几何的来源映射共享顶点身份。属性 Block 完整保存全部法线、切线、UV、颜色与自定义流，其编码由属性 Importer 决定。现有 Cluster 执行继续使用 `ClusterMeshBlock` 和它对应的 Vertex 着色器；新表示的消费在收缩阶段接入。

## Invariants
新增一种 vertex encoding 不修改 Cluster core raster/deform/shade 逻辑。

## Relations
[[Vertex-Encoding]] · [[architecture/shader/User-Stage-Interfaces]]
