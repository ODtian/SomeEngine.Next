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

## Invariants
新增一种 vertex encoding 不修改 Cluster core raster/deform/shade 逻辑。

## Relations
[[Vertex-Encoding]] · [[architecture/shader/User-Stage-Interfaces]]