# 语义 MeshAsset 与管线 Block

2026-09-16。Scene 引用 `AssetId<MeshAsset>`，不绑定 Cluster 或其他管线的具体表示。一个 MeshAsset 可以同时具有多个互相独立的强类型 Block。

`MeshBlock` 保存通用几何描述、边界和区域。`MeshMaterialsBlock` 保存区域直接引用的语义材质。`ClusterMeshBlock` 保存 Cluster 表示所需的顶点编码、payload 描述、BVH 和页摘要。将来其他扩展可添加自己的 Block，不更改 Scene 的 MeshAsset 身份。

## 生产与查询

通用 glTF 几何 Importer 只生产 MeshBlock；Cluster Importer 只生产 ClusterMeshBlock，并接收项目明确提供的顶点编码器。材质引用由独立 Importer 生产。无材质几何不被迫依赖 Standard 或 Unlit。

Importer 直接使用 glTF 库的 Mesh 对象；多个生产者在一次 Context 中写同一 Asset 的不同 Block。竞争同一种 Block 的生产者必须明确选择或报错，不依赖执行顺序覆盖。

管线直接请求认识的 Block。取得是按持久类型 ID 的索引查询，不遍历全部块，也不通过行为枚举解释通用数据袋。

## payload 与页

Cluster 的多个几何页是同一个 payload chunk 内的范围，BVH 有自己的偏移、长度和摘要。它们不是 Texture 的独立 tile chunk，内存 COW 页也不与几何页强制一一对应。既有页编码、BVH、完整性验证及流送机制继续使用。

`ClusterMeshBlock.CreateAsync(geometry, name)` 完成 BVH 取得后创建 Mesh；已经声明就绪的数据在使用期间直接可读。后续几何页通过明确的 `LoadPagesAsync` 或保留的 `MeshPayloadSource` 接入既有页调度，不能在 Mesh 的普通属性中调用 Asset 查询。

只改名字或元数据的保存可以复制原有 payload 编码，不先解压完整几何。实际修改 payload 时，偏移、长度、页摘要与 BVH 必须继续相符，通用字节 COW 不替代几何合法性检查。

## 渲染连接

顶点数据解释由用户材质和 Shader 决定。某个导入编码器不是全引擎固定顶点布局。实际 Mesh、Material 进入同一个 Game world，通过已有提取进入 Rendering world，不增加资产直接提交几何的路径。

验证覆盖冷读、共享和局部编辑、旧来源保留、页完整性、同 Mesh 多 Block、外部生产者、原生 AOT 及真实多帧绘制，见 [本次验收](Asset-Verification-2026-09-16.md)。

实现：[MeshAsset](../../../src/SomeEngine.Assets/Schema/MeshAsset.cs)、[MeshBlock](../../../src/SomeEngine.Assets/Schema/MeshBlock.cs)、[ClusterMeshBlock](../../../src/SomeEngine.Assets/Schema/ClusterMeshBlock.cs)、[几何生产者](../../../src/SomeEngine.Assets.Importers/Importers/GltfMeshImporter.cs)、[Cluster 生产者](../../../src/SomeEngine.Assets.Importers/Importers/ClusterGltfMeshImporter.cs)。
