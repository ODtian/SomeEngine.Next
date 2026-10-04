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

几何与属性分别由 `ClusterGeometryBlock` 和 `ClusterVertexAttributesBlock` 持有。构建侧通过几何的来源映射共享顶点身份。属性 Importer 交付全部来源属性及其 Shader 字节，编码选择由 Importer 和材质 Shader 对应解释。

页记录保存两个不透明字节视图的相对地址与长度。`VertexEvalContext` 提供 `vertexBlobAddress`/`vertexBlobSize` 与 `attributeBlobAddress`/`attributeBlobSize`，两者共享 `localVertexIndex`。页组合复制字节并维护拓扑、区域与 LOD，属性语义和分量由用户 Shader 决定。

默认线性、Opaque 材质以 `StaticPositionEval` 生成深度与阴影，以 `StaticVertexEval` 生成着色数据。用户的可见性、变形和细分实现按自己的函数消费所需字节。

软件 meshlet 光栅在组内共享已求值的裁剪位置，跨三角形批次复用这些几何值。用户可见性的 Varying 仍由自己的 `readLane` 实现传递。双后端公开读回覆盖这一位置缓存的行为。

## Invariants
新增一种 vertex encoding 不修改 Cluster core raster/deform/shade 逻辑。

## Relations
[[Vertex-Encoding]] · [[architecture/shader/User-Stage-Interfaces]]
