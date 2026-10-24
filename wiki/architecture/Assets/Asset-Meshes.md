# 语义 MeshAsset 与管线 Block

Scene 引用 `AssetId<MeshAsset>`。`MeshBlock` 保存通用边界、区域与来源对应，`MeshMaterialsBlock` 保存区域的语义材质；Cluster 几何与顶点属性分别保存于 `ClusterGeometryBlock`、`ClusterVertexAttributesBlock`。其它管线通过自己的 Block 共用同一资产身份。

## 生产与查询

glTF 中立组合提供语义网格与准备后的顶点流。Cluster 几何 Importer 决定顶点身份、位置、拓扑与 LOD，属性 Importer 通过 `GetBlockAsync` 获取几何及准备流，交付全部着色属性与匹配材质 Shader 的字节。网格的中立边界取自同一解码流；零三角形结果使用可选 Cluster 表示，场景装配与 Mesh 加载按可用几何创建绘制来源。构造与发现见 [Importer Discovery](Importer-Discovery.md)，数据划分见 [Block Division](Block-Division.md)。

来源中的材质编号和网格区域编号分别保存，实例的绑定按网格区域索引。静态、蒙皮、形变与二者结合的几何继续在各自分区内使用已有 Cluster 与 LOD 构建流程。

## 渲染页与读取

加载方显式读取两份 Cluster Block。`ClusterGeometryBlock.CreateAsync(shape, attributes, name)` 从该对表示创建 Mesh，取得 BVH 和最低细节页。`OpenPayloadSourceAsync(attributes)` 保留完整的渲染页来源，后续细节通过 `LoadPagesAsync(mesh, attributes, pages)` 或页流送取得。

当前 CPU 读取取得公开 Block 的数据，并为该对内容建立渲染页与 BVH。页组合维护地址、拓扑、区域与 LOD，位置及属性布局由生产者与 Shader 持有。完整位置字节与保守 AABB 来自几何 Importer，页组合直接复制这两份数据。位置段与属性段各有地址和长度；细节页沿既有 GPU 页驻留机制加载。

同一内容及其分支共用生成的页与 BVH 数组。可编辑集合的变化使下一次创建取得新数据；已返回的 Mesh 与保留来源继续引用原数组。GPU 页交付后通过已有 Mesh 变更、Game world 提取及 Rendering world 准备路径生效。

`MeshPayloadSource` 保留普通托管数组。页与 BVH 的视图具有 GC 生命周期，来源关闭后已返回的视图继续有效。纹理与 Shader 的文件字节缓存继续由 [Asset Byte Cache](Asset-Byte-Cache.md) 管理。

## 编码与渲染连接

编码和默认材质的具体表示见 [Vertex Encoding](../cluster/Vertex-Encoding.md)。深度和阴影的默认线性入口读取位置，着色入口读取完整顶点。实际 Mesh 与 Material 进入 Game world，随后由现有提取进入 Rendering world。

实现：[MeshAsset](../../../src/SomeEngine.Assets/Schema/MeshAsset.cs)、[ClusterGeometryBlock](../../../src/SomeEngine.Assets/Schema/ClusterGeometryBlock.cs)、[ClusterVertexAttributesBlock](../../../src/SomeEngine.Assets/Schema/ClusterVertexAttributesBlock.cs)。公开行为验收覆盖来源位置、完整属性、保存重开、可编辑数据、保留来源与 D3D12/Vulkan 的颜色和覆盖率读回。
