# 几何功能接入

本页收录已实现的公共 API 与执行顺序。源码入口为 [ApplicationGeometry](../../../src/SomeEngine.Runtime/ApplicationGeometry.cs)、[ApplicationScene](../../../src/SomeEngine.Runtime/ApplicationScene.cs) 和 [ClusterGeometry](../../../src/SomeEngine.Render.Cluster/ClusterGeometry.cs)。

`ApplicationScene.InstallGeometry` 注册 `IApplicationGeometryFeature`。每次场景运行调用一次 `Install`；场景结束时，在 GPU 使用和系统结束后释放它返回的资源。安装上下文提供设备、RenderDevice、持久 RenderGraph、Cluster 资源及 Rendering world。

| 安装入口 | 执行时点 | 数据访问 |
| --- | --- | --- |
| `AddGameSystem` | 每次提取前 | `ImmediateSystemContext.World` 中的游戏数据 |
| `AddExtractor` | 既有提取批次 | 源查询中的游戏数据及提取上下文 |
| `AddPrepareSystem` | 提取应用后、Cluster 驻留与实例准备前 | `RenderPrepareSystemContext.World` 中的渲染数据 |
| `AddFrameSystem` | Cluster 管线录制前 | `RenderFrameSystemContext` 中的渲染数据与图 |
| `AddCompletionObserver` | 图接受提交及结束当帧录制时 | `RenderGraphFrameResult` 中的提交完成点与资源状态 |

`OnRenderGraphSubmitted` 提供已提交 pass 的 `QueueCompletion`。使用后端完成查询或外部 fence 确认 GPU 完成后读取结果；字段与任务使用对应完成点衔接后续工作。其语义见 [Range Completions](../render-graph/Range-Completions.md)。

Mesh 准备将 `ClusterGeometry` 写入渲染实体的 `RenderClusterGeometry`。它包含根 BVH、版本、顶点编码、包围范围和驻留分配关联。Cluster 材质分配与实例生产消费该结果。原生页面与 BVH 由 Cluster 驻留系统持有，几何结果描述这组资源中的范围。

`RenderExtractionContext.UpdateMaterials` 将功能使用的材质引用交给 Rendering world。提取器通过 `RenderMaterialBinding` 为实体记录材质编号，材质准备、实例字段与 Cluster 着色沿用同一条数据流。

替换后的完整几何就绪并写入 Rendering world 后，最后一个引用结束的 Mesh 进入驻留回收。驻留系统按已接受提交的完成点归还页面和 BVH 空间，并在源读取结束后释放对应页面来源。页面编号继续递增，BVH 地址范围按完成情况复用；RenderGraph 声明当前有效的 BVH 范围。实现入口为 [ClusterMeshPreparation](../../../src/SomeEngine.Render.Cluster/ClusterMeshPreparation.cs)、[ClusterMeshes](../../../src/SomeEngine.Render.Cluster/ClusterMeshes.cs) 和 [ClusterBvh](../../../src/SomeEngine.Render.Cluster/ClusterBvh.cs)。应用可通过 `GeometryMemoryBytes` 与 `GeometryBvhMemoryBytes` 设置对应设备缓冲的容量。

下面是调用示例，变换参数用于展示额外实例的摆放：

```csharp
scene.UpdateFrame = frame =>
{
    if (frame.Index != 0) return;
    var source = frame.Scene.MeshEntities[0];
    var mesh = frame.Scene.GetMesh(source).Duplicate();
    var material = frame.Scene.GetMaterial(source);
    frame.Scene.AddEntity(mesh, [material],
        new SomeEngine.Core.Math.TransformQvvs(
            new System.Numerics.Vector3(2, 0, 6), System.Numerics.Quaternion.Identity));
};
```

`SceneEdit.ReplaceMesh` 替换实体的几何引用；`Mesh.Update` 更新共享 Mesh 的内容。两种操作通过既有 Mesh 提取进入渲染世界。

完整的功能安装调用示例位于 [ApplicationGeometryTransferTests](../../../tests/SomeEngine.Runtime.Tests/ApplicationGeometryTransferTests.cs)：游戏系统提交数值，提取器传递数值，准备系统计算上传值，帧系统录制复制，观察器按 GPU 完成点读取结果。Mesh 版本与画面切换调用示例位于 [ApplicationGeometryTests](../../../tests/SomeEngine.Runtime.Tests/ApplicationGeometryTests.cs)。

## GPU 几何构建

已实现入口为 [ClusterGpuBuilder](../../../src/SomeEngine.Render.Cluster.Pipeline/ClusterGpuBuilder.cs)，计算核心为 [cluster_build.slang](../../../assets/Shaders/cluster_build.slang)。`Reserve` 在渲染准备阶段从现有 Cluster 页堆和 BVH 分配范围，返回描述该范围的 `ClusterGeometry`。`Record` 在首次绘制前录制生成工作，接收当帧 `ClusterGpuMeshInput` 中的四个 `GraphBufferSlice`：`float3` 位置、用户属性字节、`uint` 索引和计数。计数依次存储顶点数量与索引数量；当前入口生成一个 Cluster，支持至多 64 个顶点和 124 个三角形。

调用方通过 `ClusterGpuVertexEncoder` 提供 Slang Shader、计算入口、`VertexEncoding` 和顶点字节容量。该计算入口调用 `BuildCluster`，由调用方的 `IClusterVertexEncode` 实现写入顶点字节，公共计算核心写入 Cluster 元数据、拓扑和 BVH。配套 `IVertexEvaluate` 解释相同编码的顶点字节；使用公共材质入口包含文件时，通过 `CLUSTER_VERTEX_ENCODING` 声明编码身份。

`Record` 返回生成 pass 的 `GraphPassId`。Cluster 将当帧 BVH 和页面读取排在生成工作之后，并在本帧重新计算场景包围范围；主体渲染图继续保持。完成观察器通过 `TryGetPassCompletion` 取得该 pass 的队列完成点。移除几何的渲染引用后，在准备阶段调用 `Release`，Cluster 驻留系统在已提交使用完成后回收范围。生成与回收的数据持有见 [ClusterRenderResources.Gpu](../../../src/SomeEngine.Render.Cluster/ClusterRenderResources.Gpu.cs) 和 [ClusterMeshes.Gpu](../../../src/SomeEngine.Render.Cluster/ClusterMeshes.Gpu.cs)。

完整调用示例为 [GpuClusterExample](../../../samples/DefaultGame/GpuClusterExample.cs)。其中两米正方形和 `float3` 位置加 `float2` UV 的布局属于示例数据；[GpuClusterRenderingTests](../../../tests/SomeEngine.Runtime.Tests/GpuClusterRenderingTests.cs) 通过公开应用入口验证生成后的首帧图像、用户 UV 属性和 GPU 完成点。
