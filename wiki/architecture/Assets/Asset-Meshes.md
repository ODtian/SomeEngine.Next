# 语义 MeshAsset 与管线 Block

2026-09-30。Scene 引用 `AssetId<MeshAsset>`，不绑定 Cluster 或其他管线的具体表示。一个 MeshAsset 可以同时具有多个互相独立的强类型 Block。

`MeshBlock` 保存通用几何描述、边界和区域。`MeshMaterialsBlock` 保存区域直接引用的语义材质。`ClusterMeshBlock` 保存 Cluster 表示所需的顶点编码、payload 描述、BVH 和页摘要。将来其他扩展可添加自己的 Block，不更改 Scene 的 MeshAsset 身份。

## 生产与查询

通用 glTF 几何 Importer 只生产 MeshBlock；Cluster Importer 只生产 ClusterMeshBlock，并接收项目明确提供的顶点编码器。材质引用由独立 Importer 生产。无材质几何不被迫依赖 Standard 或 Unlit。

Importer 直接使用 glTF 库的 Mesh 对象；多个生产者在一次 Context 中写同一 Asset 的不同 Block。竞争同一种 Block 的生产者必须明确选择或报错，不依赖执行顺序覆盖。

管线直接请求认识的 Block。取得是按持久类型 ID 的索引查询，不遍历全部块，也不通过行为枚举解释通用数据袋。

## CPU 程序几何

`SomeEngine.Geometry.Cluster` 持有 CPU Cluster 构建核心。`ClusterMeshBuilder.Build<TAttributes>` 接收位置、用户属性、三角形索引、材质分组和配套的 `IClusterVertexEncoder<TAttributes>`，直接返回所有页面与 BVH 已就绪的 `Mesh`。材质分组以索引起点、索引数量和材质编号表达，相邻分组可以引用共同输入顶点。

`ClusterBuildStrategy.Realtime` 构建叶子 cluster 与 BVH。`ClusterBuildStrategy.Offline` 执行顶点焊接、缓存顺序优化、完整简化与 LOD 层级构建。两种策略的结果通过相同的 Mesh 提取和驻留流程消费。用户编码器返回编码身份、顶点字节及包围范围，材质的 `IVertexEvaluate` 解释这些字节；`StandardClusterVertexEncoder` 是一种可显式选择的编码器。

导入层的 `ClusterBuilder.ProcessRaw` 与 `ProcessMesh` 调用该核心的离线策略，再通过 `CreateAsset` 组织 `MeshBlock`、`MeshMaterialsBlock` 和 `ClusterMeshBlock`。`CreateAsset` 先捕获 Mesh 当前数据，资产保存使用这份已完成几何。公开程序几何调用示例见 [ProceduralClusterExample](../../../samples/DefaultGame/ProceduralClusterExample.cs)。

## payload 与页

Cluster 的多个几何页是同一个 payload chunk 内的范围，BVH 有自己的偏移、长度和摘要。它们不是 Texture 的独立 tile chunk，也不对应通用 CPU 内存页。既有页编码、BVH、完整性验证及流送机制继续使用。

`ClusterMeshBlock.CreateAsync(geometry, name)` 完成 BVH 和 LowestDetailPageIndices 指定的最低细节几何后返回 Mesh。后续细节使用显式 LoadPagesAsync 或 MeshPayloadSource 取得。Mesh 的普通属性不调用 Asset 查询。

同一 Block 的未变分支共享几何范围描述。页和 BVH 的实际长度数组采用统一预算缓存，Mesh 直接强引用已加载内容；重复创建复用缓存数据。TryGetPage 返回普通 ReadOnlyMemory，Bvh 提供已取得的托管视图；Mesh 不实现托管数据释放协议。

新增几何页会更新 Mesh 的变更状态并使提取缓存失效。旧 CaptureData 仍保留原数据，新提取能够观察新页；Mesh 不保存所有历史提取结果。最低表示规则见 [Bootstrap](Bootstrap-Streaming.md)。

只改名字或元数据的保存可以复制原有 payload 编码，不先解压完整几何。实际修改 payload 时，偏移、长度、页摘要与 BVH 必须继续相符，通过 SetPayload 替换完整数据并重新建立相应摘要，不原地覆写已共享字节。

## 渲染连接

顶点数据解释由用户材质和 Shader 决定。某个导入编码器不是全引擎固定顶点布局。实际 Mesh、Material 进入同一个 Game world，通过已有提取进入 Rendering world，不增加资产直接提交几何的路径。

本次冷读、数组共享、描述修改、旧来源保留、页完整性、原生 AOT 和三场景反馈见 [2026-09-30 验证](Asset-Verification-2026-09-30.md)。同 Mesh 多 Block、外部生产者及原工程的多帧绘制证据保留在 [2026-09-16 记录](Asset-Verification-2026-09-16.md)。

实现：[CPU 构建入口](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Input.cs)、[CPU 编码与页面构建](../../../src/SomeEngine.Geometry.Cluster/ClusterMeshBuilder.Pages.cs)、[MeshAsset 组装](../../../src/SomeEngine.Assets.Importers/Importers/ClusterBuilder.Assets.cs)、[MeshAsset](../../../src/SomeEngine.Assets/Schema/MeshAsset.cs)、[MeshBlock](../../../src/SomeEngine.Assets/Schema/MeshBlock.cs)、[ClusterMeshBlock](../../../src/SomeEngine.Assets/Schema/ClusterMeshBlock.cs)、[几何生产者](../../../src/SomeEngine.Assets.Importers/Importers/GltfMeshImporter.cs)、[Cluster 生产者](../../../src/SomeEngine.Assets.Importers/Importers/ClusterGltfMeshImporter.cs)。
