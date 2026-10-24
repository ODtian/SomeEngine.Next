# 地形放置与分块

本页维护已实现的架构、公开接口与调用方法。源码入口为 [TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs)、[TerrainSurface](../../../src/SomeEngine.Resources/Terrain/TerrainSurface.cs) 和 [TerrainGeometryFeature](../../../src/SomeEngine.Runtime/TerrainGeometryFeature.cs)。字段创建与编辑见 [CPU 字段](Terrain-Fields.md)。

## 数据职责

表面读取、固定版本、权重混合和局部体素初始化见[地形表面组合](Terrain-Surfaces.md)。空间块统一消费 Terrain 的表面根及对应采样邻域。

`TerrainPlacement` 持有 Terrain 引用、变换、材质绑定、选定的三维根块范围和 LOD 设置。`TerrainScene` 管理放置编号、块实体、共享几何和构建任务。同一个 Terrain、块坐标、LOD 与过渡面配置对应一份几何；每个放置保持各自的变换和材质。`TerrainChunk` 组件记录放置编号、三维坐标、LOD、过渡面、已提交的源版本和几何编号。字段页及样本数据由 Terrain 字段持有。

`TerrainChunkGrid` 给出每边单元数和最细采样间距；LOD 每增加一级，单元边长加倍。`TerrainChunkRange` 的最大坐标为排除端点。高度场的最小、最大高度用于限制每列的竖向单元访问。

## 更新与呈现

`TerrainGeometryFeature` 通过 `ApplicationScene.InstallGeometry` 安装三个游戏系统，按以下顺序运行：

1. `TerrainCommitSystem` 收集字段范围变化，并把已完成构建更新到块版本及 Mesh。
2. `TerrainLodSystem` 根据根块范围、相机距离和相邻层级关系选择替换块。
3. `TerrainMeshSystem` 捕获受影响块需要的表面版本，提交对应几何构建。

该功能同时安装地形请求提取、GPU 准备、帧录制与完成观察。每个 Scene 为使用的 Terrain 持有独立的 `TerrainSource` 实体，请求沿同一条 Game world 到 Rendering world 的提取路径传递；最后一个放置移除后，来源实体继续保持到构建和请求消费完成。GPU 页的按需 CPU 读取为后续网格任务提供样本，详见 [GPU 编辑](Terrain-Gpu-Editing.md)。

高度编辑提交产生 `HeightFieldChange`。场景结合写入范围和采样邻域选择需要更新的块；一次共享 Terrain 的构建完成后，所有引用对应几何的放置一起取得该版本。构建期间保留当前块实体和几何，完成时共同应用所捕获的块选择、放置变换和材质。相机输入、2∶1 邻接和接缝规则见[地形 LOD](Terrain-Lod.md)。

体素编辑提交产生 [VoxelFieldChange](../../../src/SomeEngine.Resources/Terrain/VoxelFieldChange.cs)，按三维写入范围和采样邻域选择空间块。表面提取在对应三维单元中生成零等值面；提交和提取继续使用相同的三个游戏系统。

CPU 路径的块实体使用 `WorldTransform`、`MeshInstance` 和 `MeshMaterialBinding`，由现有 [Mesh 提取](../rendering/Extraction.md) 进入 Rendering world。每个 Scene 通过自己持有的块实体索引应用几何与版本；`GetChunks` 返回该 Scene 当前提交的块版本，同一 World 中的多个 Scene 分别维护各自的局部编号。`WaitForBuildsAsync` 等待当前已提交的构建任务；接着运行的提交系统应用结果。

## 表面与属性

[MarchingCubesTerrainMesher](../../../src/SomeEngine.Terrain/Meshing/MarchingCubesTerrainMesher.cs) 配套生成普通 Marching Cubes 单元和 Transvoxel 过渡单元，表及许可保存在同目录。`ITerrainMesher` 接收已就绪的标量表面与带层级、过渡面的 `TerrainChunkBounds`，产出位置、索引及属性采样点；完整坐标规则见[地形 LOD](Terrain-Lod.md)。

`TerrainClusterAdapter<TAttributes>` 将调用方的 `ITerrainAttributeGenerator<TAttributes>`、`IClusterVertexEncoder<TAttributes>` 与 `ClusterBuildStrategy` 连接到通用 CPU Cluster 构建器。属性生成器提供顶点属性、三角形材质编号、额外采样邻域和 `Fields` 声明。场景构建与 [TerrainClusterCache](../../../src/SomeEngine.Terrain/TerrainClusterCache.cs) 离线构建将声明传给同一次[附加字段捕获](Terrain-Surfaces.md#附加字段)，属性回调通过固定表面读取取得所需字段。材质的顶点求值与调用方选用的编码相配套。

GPU 构造入口接收 `TerrainClusterAdapter`，复用本页的放置、块编号、变化范围和完整构建批次。固定字段输入、GPU 表面与属性、原生 Cluster 构建和可见完成入口统一见 [GPU 地形几何](Terrain-Gpu-Geometry.md)。

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

[VoxelTerrainExample](../../../samples/DefaultGame/VoxelTerrainExample.cs) 展示三维体素表面：`Install` 安装已有地形系统，`AddPlacement` 设置材质和放置变换，`WriteSphere` 在明确的编辑区域内写入球体标量。每米一个采样点、一米半径及 `[-3,4)` 的样本区域属于示例参数。示例将球心从 `(-1,0,0)` 移到 `(1,0,0)`，这两处粗采样表面的零等值面均由六个轴向交点和八个三角形组成。`VoxelTerrainRenderingTests` 通过应用入口移动该表面，并读取对应标量版本。
