# Vertex Encoding

## Definition
Vertex encoding 是可替换的 asset-build 与 shader decode 实现，不属于 Cluster ABI。

## Standard implementation
内置 Standard encoder 可以继续使用 cluster-local position quantization、variable bit width、normal/tangent compression、variable UV precision 与 bitstream attributes。

扩展阶段保留组合编码 `someengine.vertex.standard.cluster.v1`。新 `ClusterGeometryBlock` 使用 `someengine.cluster.positions.uint16.v1`：逐 cluster 的 origin、二次幂 step 与 little-endian uint16 XYZ，量化规则与组合编码一致。`ClusterVertexAttributesBlock` 使用 `someengine.cluster.attributes.float64.v1`：每个 cluster 按几何的顶点顺序保存全部着色属性，每个流采用 little-endian float64；属性记录名称、分量数及逐顶点实际分量数。该编码无损保留准备流的值，包括来源 uint32、自定义矩阵与符号零。

几何与属性共同携带 `VertexIdentity`，对应同一次几何焊接与重排。其它编码通过替换属性 Importer 交付；运行时迁移由后续收缩阶段完成。

## Invariants
- opaque 不意味着固定 stride AoS。
- cook 输出记录 encoding compatibility；对应 Vertex implementation 在 build/pipeline creation 前匹配。
- 不匹配不进入 draw hot path 再验证。

## Relations
[[Opaque-Vertex-Data]]
