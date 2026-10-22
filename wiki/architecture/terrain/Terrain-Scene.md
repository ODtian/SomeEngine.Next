# 地形放置与 CPU 分块

本页维护已实现的架构、公开接口与调用方法。源码入口为 [TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs)、[TerrainSurface](../../../src/SomeEngine.Resources/Terrain/TerrainSurface.cs) 和 [TerrainGeometryFeature](../../../src/SomeEngine.Runtime/TerrainGeometryFeature.cs)。字段创建与编辑见 [CPU 字段](Terrain-Fields.md)。

## 数据职责

`Terrain.Surface` 持有标量表面根。`HeightSurface` 使用高度场的 `F(x,y,z) = y - H(x,z)`，负值表示实体内部。`TerrainSurfaceRead` 持有固定版本的读取数据；`ReadCpuAsync` 在样本就绪后返回，`ScheduleRead` 将版本依赖和页读取访问交给现有 Scheduler，随后执行调用方的并行工作。

`TerrainPlacement` 持有 Terrain 引用、变换、材质绑定、选定的三维块范围和 LOD。`TerrainScene` 管理放置编号、块实体、共享几何和构建任务。同一个 Terrain、块坐标与 LOD 对应一份几何；每个放置保持各自的变换和材质。`TerrainChunk` 组件记录放置编号、三维坐标、LOD、已提交的源版本和几何编号。字段页及样本数据由 Terrain 字段持有。

`TerrainChunkGrid` 给出每边单元数和最细采样间距；LOD 每增加一级，单元边长加倍。`TerrainChunkRange` 的最大坐标为排除端点。高度场的最小、最大高度用于限制每列的竖向单元访问。

## 更新与呈现

`TerrainGeometryFeature` 通过 `ApplicationScene.InstallGeometry` 安装三个游戏系统，按以下顺序运行：

1. `TerrainCommitSystem` 收集字段范围变化，并把已完成构建更新到块版本及 Mesh。
2. `TerrainLodSystem` 根据各放置指定的固定 LOD 和块范围维护实体。
3. `TerrainMeshSystem` 捕获受影响块需要的表面版本，通过 Scheduler 构建几何。

该功能同时安装地形请求提取、GPU 准备、帧录制与完成观察。块实体的 `TerrainSource` 引用使请求进入同一条 Game world 到 Rendering world 的提取路径，GPU 页的按需 CPU 读取为后续网格任务提供样本，详见 [GPU 编辑](Terrain-Gpu-Editing.md)。

高度编辑提交产生 `HeightFieldChange`。场景结合写入范围和采样邻域选择需要更新的块；一次共享 Terrain 的构建完成后，所有引用对应几何的放置一起取得该版本。构建期间保留当前几何。

块实体使用 `WorldTransform`、`MeshInstance` 和 `MeshMaterialBinding`，由现有 [Mesh 提取](../rendering/Extraction.md) 进入 Rendering world。`GetChunks` 返回游戏世界当前提交的块版本。`WaitForBuildsAsync` 等待当前已提交的构建任务；接着运行的提交系统应用结果。

## 表面与属性

[MarchingCubesTerrainMesher](../../../src/SomeEngine.Terrain/Meshing/MarchingCubesTerrainMesher.cs) 使用 Transvoxel 的普通 Marching Cubes 表，表及许可保存在同目录。`ITerrainMesher` 接收已就绪的标量表面，产出位置、索引及属性采样点。

`TerrainClusterAdapter<TAttributes>` 将调用方的 `ITerrainAttributeGenerator<TAttributes>`、`IClusterVertexEncoder<TAttributes>` 与 `ClusterBuildStrategy` 连接到通用 CPU Cluster 构建器。属性生成器提供顶点属性、三角形材质编号和额外采样邻域。材质的顶点求值与调用方选用的编码相配套。

## 公开调用示例

[HeightTerrainExample](../../../samples/DefaultGame/HeightTerrainExample.cs) 是可编译的双放置示例。四米方形、每米一个采样点、两层竖向块及两个放置的位置属于示例参数。

```csharp
using var scheduler = new Scheduler();
var ground = new HeightTerrainExample(scheduler);
ground.Install(application);
application.UpdateFrame = frame =>
{
    if (frame.Index == 0)
        ground.AddPlacements(firstMaterial, secondMaterial);
    if (frame.Index == 60)
        ground.Heights.EditCpu(new HeightDeltaBrush(ground.Heights.Grid.Range, 1f));
};
await Application.RunAsync(args, application: application);
```

表面读取、平面几何、局部块版本与应用画面的行为入口分别为 `TerrainSurfaceTests`、`TerrainMesherTests`、`TerrainSceneTests` 和 `HeightTerrainRenderingTests`。
