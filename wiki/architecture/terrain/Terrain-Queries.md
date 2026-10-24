# 地形查询与碰撞几何

本页维护已实现的固定版本查询与碰撞数据职责（Wiki 类别 1）及公共接口用法（类别 2）。主要源码为 [TerrainQueries](../../../src/SomeEngine.Resources/Terrain/TerrainQueries.cs)、[TerrainSurfaceRead](../../../src/SomeEngine.Resources/Terrain/TerrainSurface.cs) 和 [TerrainCollision](../../../src/SomeEngine.Terrain/TerrainCollision.cs)。

## 查询与来源

查询消费已经就绪的固定 CPU 读取。字段的 `ReadCpuAsync` 在调用时捕获版本，等待该版本的页面工作完成；表面的 `ReadCpuAsync` 在同一 Terrain 捕获范围内取得所依赖的字段与组合参数。CPU、文件页面和 GPU 页面沿同一读取入口提供样本，GPU 页面等待实际设备完成后提供 CPU 数组。

| 要素 | 高度查询 | 距离查询 | 表面查询 |
| --- | --- | --- | --- |
| 固定输入 | `HeightFieldRead` | `VoxelFieldRead` | `TerrainSurfaceRead` |
| 调用 | `QueryHeight(position, normalDistance)` | `QueryDistance(position, normalDistance)` | `Query(position, normalDistance)` |
| 位置 | 局部米制 X/Z | 局部米制 X/Y/Z | 局部米制 X/Y/Z |
| 返回值 | `TerrainHeightQuery.Height`，双线性高度 | `TerrainDistanceQuery.Distance`，三线性有符号距离 | `TerrainSurfaceQuery.Value`，表面标量 |
| 返回法线 | 高度表面的向外单位法线 | 距离梯度的向外单位法线 | 表面梯度的向外单位法线 |
| 来源 | `Source` 与单个 `FieldVersion` | `Source` 与单个 `FieldVersion` | `Source`、`Revision` 与 `FieldVersions` |

`normalDistance` 是中心差分的单侧米制间距。读取范围包含查询位置与各坐标轴上的差分邻域。高度查询沿 X/Z 取差分，表面法线由 `(-∂H/∂x, 1, -∂H/∂z)` 归一化得到；距离与表面查询沿 X/Y/Z 取差分。

距离查询消费具有距离含义的字段，幅值单位为米。任意编辑或组合后的标量可通过[距离重建](Terrain-Distance.md)取得对应距离字段。`HeightSurface` 的值为 `y - H(x,z)`，表达竖向高度差；`BlendSurface` 的值按其[组合公式](Terrain-Surfaces.md)求得。

`TerrainFieldVersion` 记录 `Kind`、Terrain 内对应字段集合的 `Id` 和该字段的 `Revision`。字段读取直接提供 `FieldId` 与 `Version`。表面读取提供 `Bounds`、捕获时的 Terrain `Revision` 和参与字段的 `FieldVersions`；Blend 按来源顺序合并字段记录。查询结果持有固定读取，后续字段编辑继续形成新版本。

表面读取也可按 `TerrainFieldInputs` 捕获材料使用的附加字段，访问入口与批次范围见[附加字段](Terrain-Surfaces.md#附加字段)。

## 区域与分辨率

`TerrainCollisionGrid(Bounds, CellsX, CellsY, CellsZ)` 表达局部米制长方体和三条轴的单元数。`CellSize` 为 `(Bounds.Max - Bounds.Min) / (CellsX, CellsY, CellsZ)`，调用方分别选择区域起点、区域大小与每轴分辨率。单元数为正，区域的每轴长度为正。

`BuildCollisionMeshAsync(grid, mesher)` 在进入时捕获表面版本，使用现有 `ScheduleRead` 等待 CPU 样本完成，再由 Job 生成几何。读取范围通过 `grid.GetReadBounds(mesher.ReadBorder)` 扩展，三轴分别采用其单元间距。已有 `TerrainSurfaceRead` 可调用 `BuildCollisionMesh(grid, mesher)`，读取范围覆盖所选网格与 mesher 所需邻域。

默认 mesher 为 [MarchingCubesTerrainMesher](../../../src/SomeEngine.Terrain/Meshing/MarchingCubesTerrainMesher.cs)。碰撞生成把所选网格的每个单元交给同一 `ITerrainMesher`；输入采样与输出顶点使用同一个网格到地形局部米制的映射。所选分辨率直接决定本次几何采样，调用方也可传入自己的 mesher。

`TerrainCollisionMesh` 持有以下数据：

- `Source`：生成几何所用的固定表面读取，包含字段版本与读取区域。
- `Grid`：调用方选择的区域与三轴分辨率。
- `Positions`：地形局部米制顶点。
- `Indices`：按三个索引组成三角形的 `uint` 数组。

物理接入方接收顶点与索引，并按对应 [TerrainPlacement](Terrain-Scene.md) 应用放置变换。刚体、接触、约束和求解状态由物理域保持。接入方通过 `mesh.Source.Query(...)` 查询生成该网格的同一版本表面。

## 调用示例

以下为使用示例：高度为 `0.5` 米、字段间距为 `0.5` 米，碰撞区域和 `5 × 6 × 4` 单元数均为示例参数。

```csharp
using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;
using SomeEngine.Terrains;

using var scheduler = new Scheduler();
using var terrain = new Terrain(new TerrainEditing(scheduler));
HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(8, 7, 4, .5f), .5f);
var surface = new HeightSurface(heights);
terrain.Surface = surface;

var grid = new TerrainCollisionGrid(
    new TerrainBounds(new(.25f, -.25f, .5f), new(2.75f, 1.25f, 2.5f)), 5, 6, 4);
ValueTask<TerrainCollisionMesh> request = surface.BuildCollisionMeshAsync(grid);
await heights.EditCpu(new HeightDeltaBrush(heights.Grid.Range, 1f)).WaitAsync();

TerrainCollisionMesh mesh = await request;
TerrainSurfaceQuery query = mesh.Source.Query(new Vector3(1f, .75f, 1f), .1f);
float heightDifference = query.Value; // 捕获表面的竖向高度差为 0.25 米。
ReadOnlyMemory<TerrainFieldVersion> fields = query.FieldVersions;
```

GPU 编辑后的查询与碰撞使用相同入口。应用继续通过 [GameWorld/RenderWorld 的提取与 GPU 完成路径](Terrain-Gpu-Editing.md)处理该 Terrain，读取任务在对应版本完成后继续执行。

## 行为检查入口

- [TerrainCollisionTests](../../../tests/SomeEngine.Terrain.Tests/TerrainCollisionTests.cs)：高度、体素与 Blend 的版本查询，指定长方体和分辨率的网格，以及后续编辑后的固定来源。
- [TerrainCollisionGpuTests](../../../tests/SomeEngine.Runtime.Tests/TerrainCollisionGpuTests.cs)：经公开应用入口处理 GPU 距离重建，查询和碰撞消费该捕获版本，字段随后继续编辑。

返回[地形导航](../Terrain.md)。
