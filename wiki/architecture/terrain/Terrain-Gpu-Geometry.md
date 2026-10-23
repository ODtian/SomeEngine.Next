# GPU 地形几何

本页维护已实现的公共接口、数据职责与调用方法。源码入口为 [TerrainClusterAdapter.Gpu](../../../src/SomeEngine.Terrain/TerrainClusterAdapter.Gpu.cs)、[TerrainGpuMesher](../../../src/SomeEngine.Terrain/Meshing/TerrainGpuMesher.cs)、[RenderTerrainGeometry](../../../src/SomeEngine.Terrain/RenderTerrainGeometry.cs) 和 [terrain_height_surface.slang](../../../assets/Shaders/terrain_height_surface.slang)。字段编辑见 [GPU 字段编辑](Terrain-Gpu-Editing.md)，空间块见 [地形放置与分块](Terrain-Scene.md)，原生构建见 [GPU 几何构建](../rendering/Geometry-Features.md#gpu-几何构建)。

`TerrainScene(TerrainClusterAdapter, TerrainChunkGrid)` 使用 GPU 几何适配器。它与 CPU 构造入口共用放置、共享几何编号、受影响块集合和每个 Terrain 的完整构建批次。`TerrainGeometryFeature` 安装同一组游戏系统和地形请求提取，并增加 GPU 几何的准备、帧录制与完成观察。

`TerrainClusterAdapter` 接收 `ITerrainMesherGpu`、`TerrainGpuAttributes` 和 `ClusterGpuVertexEncoder`。`MarchingCubesTerrainMesherGpu` 消费捕获的 `HeightSurface`，使用与 CPU 提取器相同的 Transvoxel 普通单元表。高度字段在地形局部 X/Z 米制位置进行双线性采样，标量为 `y-H(x,z)`；每列先计算高度范围，再遍历表面附近的竖向单元。输出为位置、索引、顶点与索引计数，以及具有来源位置和法线的采样点。

`ITerrainMesherGpu.GetCapacity` 给出预留缓冲所需的顶点和三角形数量上限，`Record` 通过 `TerrainGpuMesherContext.AddCompute` 录制提取入口。普通单元的上限为 12 个顶点和 5 个三角形。实际计数由 GPU 写出，并限定后续属性和 Cluster 构建读取的有效前缀。

`TerrainGpuAttributes` 指定属性 Shader、入口、每顶点字节数、额外读取邻域和本次构建的 `MaterialIndex`。属性入口以每组 64 个线程运行，读取 `TerrainSamples` 和 `TerrainCounts`，写入 `TerrainAttributes`。每个采样点依次存储 `float3` 来源位置与 `float3` 法线；位置和索引分别使用 `float3` 与 `uint`。属性字节由配套 `ClusterGpuVertexEncoder` 和材质 `IVertexEvaluate` 解释。

批次提交时，在 Terrain 的数据锁内固定字段页、源版本和所需范围；几何请求与字段请求一起保留到图提交确认，并由现有 `TerrainRenderExtractor` 传到 Rendering world。`RenderTerrainData` 继续持有设备字段页和页表。`RenderTerrainGeometry` 使用它提供的固定源输入，在当帧图中依次录制表面提取、用户属性和通用 Cluster 构建。原生页堆与 BVH 由 Cluster 驻留系统持有。

完成状态分别通过以下公开入口观察：

| 阶段 | 版本与完成入口 | 完成含义 |
| --- | --- | --- |
| 字段编辑 | `TerrainEdit.Revision`、`Completion`、`WaitAsync` | 对应字段页的写入完成 |
| GPU 几何 | `TerrainScene.GetGpuBuild(terrain).Revision`、`Completion` | 该批次的表面、属性、Cluster 页面和 BVH 完成 |
| 可见绑定 | 同一 `TerrainGpuBuild.Visibility` | 渲染准备已经绑定整批几何版本 |

`TerrainScene.WaitForBuildsAsync` 等待已提交的几何工作。游戏侧 `TerrainCommitSystem` 在整批构建完成后一起推进对应 `TerrainChunk.Revision`；`GetChunks` 读取这些提交版本。随后提取器传递块版本、变换和材质，渲染准备按版本绑定共享 `ClusterGeometry`。生成期间保留当前画面，新批次绑定后通过 `ClusterGpuBuilder.Release` 归还旧范围，实际复用由最后使用完成点决定。

[GpuHeightTerrainExample](../../../samples/DefaultGame/GpuHeightTerrainExample.cs) 是可编译示例。四米方形、每米一个采样点、两层竖向块、两个放置及 `float3` 位置、`float3` 法线、`float2` UV 编码都属于示例选择：

```csharp
using var scheduler = new Scheduler();
using var ground = await GpuHeightTerrainExample.CreateAsync(scheduler, contentDirectory);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0) ground.AddPlacements(firstMaterial, secondMaterial);
    if (frame.Index == 60) ground.AddHeight(1f);
};
await Application.RunAsync(args, application: application);
```

CPU 查询通过显式 `HeightField.ReadCpuAsync(range)` 请求所需样本。公开应用行为入口为 [TerrainGpuGeometryTests](../../../tests/SomeEngine.Runtime.Tests/TerrainGpuGeometryTests.cs)，对照相同字段、笔刷、相机和材质的 CPU/GPU 画面，并观察批次完成、可见绑定与块版本。
