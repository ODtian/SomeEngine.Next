# Vertex Encoding

## Definition
Vertex encoding 由属性 Importer 与材质 Shader 对应实现。Cluster 维护不透明的几何和属性字节地址、长度，以及共同的局部顶点编号。

## Standard implementation
`ClusterGeometryBlock` 使用 `someengine.cluster.positions.uint16.v1`：每个 cluster 的 origin、二次幂 step 与 little-endian uint16 XYZ。位置 Importer 交付完整编码字节及独立的保守 AABB，量化继续使用原有规则。几何 Block 保存顶点身份、来源映射、拓扑、区域、变形分区和 LOD；页组合直接搬运编码和边界。默认 CPU 解码入口和 PositionOrigin、PositionStep 读取同一份 PositionData 头部；保存重开保留这份编码字节，运行时页组合按不透明字节工作。

`ClusterVertexAttributesBlock` 的公开属性记录完整保留来源流：名称、分量数、逐顶点实际分量数和 little-endian float64 数值，包含全部 UV、颜色与自定义属性。保存重开后可通过 `ReadComponent` 读取来源数值、uint32 精度与符号零。

`StandardClusterAttributesEncoder` 属于 Importer 实现，默认属性 Importer 另行在每个 cluster 的 `Data` 中生成 `someengine.cluster.attributes.standard.v1` 的 Shader 字节。NORMAL 与 TANGENT 使用原有 signed-normalized 8 位量化，TEXCOORD_0 使用 cluster 内 origin、二次幂 step 与 uint16 UV，其余流继续携带 float64 数值。名称、分量数与缺省信息随编码保留。该格式的布局与解码属于属性生产者和 `vertex_evaluate.slang` 中的默认材质实现。

采用紧凑 Shader 表示的原因是：直接上传公开 float64 记录会放大 GPU 页数据，挤压既有几何驻留预算。来源记录保留完整值，默认材质继续采用迁移前的属性量化精度；额外的流交付给用户 Shader。几何划分与 LOD 构建使用已有算法。

glTF 与 Raw 来源入口共用局部索引和 `StandardClusterPositionEncoder`，零范围与量化误差采用同一保守包围规则。

两份 Block 携带相同 `VertexIdentity`。渲染页组合复制 `Data`，以位置和属性两段字节交付给 Vertex 接口。材质入口的兼容性标识由位置与属性的标识组合而成，在管线建立时匹配。

Cluster 蒙皮影响与逐目标形变增量各有自己的 Block 和编码标识，按几何 Block 的顶点映射保存完整数值。默认编码沿用流描述布局并保留 float64 值；材质 Importer 可在页组合之前将所需数据编入自己的属性字节。数据与入口见 [Deformation Import](Deformation-Import.md)。

## Invariants
- 用户编码可以定义自己的 AoS、SoA、bit packing 或私有头部。
- 更换属性编码通过替换 Importer 与对应 Shader 完成。
- 编码匹配在建立管线时完成，顶点读取按用户函数执行。

## Relations
[[Opaque-Vertex-Data]] · [[architecture/Assets/Block-Division]]
