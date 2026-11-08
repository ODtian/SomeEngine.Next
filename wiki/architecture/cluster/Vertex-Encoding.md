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

## CPU 编码批次

本节记录已实现的公共编码入口和页面构建数据流。程序几何通过 `ClusterMeshBuilder.Build` 传入位置、用户属性、索引、材质分组与 `ClusterBuildStrategy`。实时策略构建叶子 Cluster，离线策略沿既有焊接、优化与 LOD 核心完成几何划分，随后进入同一批次编码和页面填充路径。

`IClusterVertexEncoder<TAttributes>` 与 `IClusterVertexEncoder` 保留 `Encode` 作为用户字节及边界的生产入口。可选的 `CreateBatchEncoder()` 在协调阶段按批次调用，创建过程具有单一发起者。默认返回 `null` 时，整次构建共享原编码器，在同一锁内调用 `Encode` 并复制返回字节，因此原编码器可继续复用自己的返回缓冲。提供批次编码器时，该实例使用相同编码标识，并持有本批独立可变状态；无状态编码器可显式返回自身，内置 `StandardClusterVertexEncoder` 使用这一入口。

每个编码批次持有自己的局部顶点映射、索引及压缩输出。编码结果就绪后，协调阶段按原 Cluster 顺序归并长度，确定页面编号及记录、顶点字节和三角形的区间。填充工作只写分配给自己的页面，完成后按页面顺序交付完整 Mesh。编码与填充消费 `JobSystem.ExecuteParallel` 的共享执行器；同步返回覆盖全部参与者退出和临时输入的使用结束。实现入口为 [编码接口](../../../src/SomeEngine.Geometry.Cluster/ClusterVertexEncoder.cs)、[程序构建输入](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Input.cs) 和 [页面构建](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Pages.cs)。

资产导入先固定来源映射和几何记录，再独立编码位置及属性；自定义 Raw 编码使用相同的批次工厂与共享编码器兼容路径。Block 在全部结果就绪后集中组装。`ClusterGeometryBlock.CreateAsync` 消费两份 Block，页面组合固定不透明字节和材料记录，归并长度后按独立页面区间填充。程序 Mesh 与资产 Block 继续使用各自既有的页面表示，位置及属性的解释由对应用户编码器与 Shader 持有。实现入口为 [几何编码](../../../src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Geometry.cs)、[属性导入](../../../src/SomeEngine.Assets.Importers/Importers/ClusterVertexAttributesImporter.cs)、[Raw 编码](../../../src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Assets.cs) 和 [资产页面组合](../../../src/SomeEngine.Assets/Pipeline/ClusterMeshData.cs)。

公开行为入口为 [CPU 构建与共享编码器](../../../tests/SomeEngine.Resources.Tests/ClusterMeshBuilderTests.cs)、[资产保存重开与页面消费](../../../tests/SomeEngine.Assets.Tests/Assets/ClusterBuilderTests.cs) 和 [实时及离线程序几何呈现](../../../tests/SomeEngine.Runtime.Tests/ProceduralClusterRenderingTests.cs)。

## Invariants
- 用户编码可以定义自己的 AoS、SoA、bit packing 或私有头部。
- 更换属性编码通过替换 Importer 与对应 Shader 完成。
- 编码匹配在建立管线时完成，顶点读取按用户函数执行。

## Relations
[[Opaque-Vertex-Data]] · [[architecture/Assets/Block-Division]]
