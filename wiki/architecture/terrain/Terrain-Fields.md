# 地形 CPU 字段

本页维护已实现的字段数据职责（Wiki 类别 1）与公开 CPU 接口用法（类别 2）。主要源码为 [Terrain](../../../src/SomeEngine.Resources/Terrain/Terrain.cs)、[HeightField](../../../src/SomeEngine.Resources/Terrain/HeightField.cs)、[VoxelField](../../../src/SomeEngine.Resources/Terrain/VoxelField.cs) 和 [TerrainEditing](../../../src/SomeEngine.Resources/Terrain/TerrainEditing.cs)。公共类型位于 `SomeEngine.Resources` 命名空间。

## 字段与坐标

`Terrain` 持有高度场、体素场及共同的版本序列。每个字段记录自己的 `Terrain`、名称、网格定义和当前提交版本；`Id` 是该字段在对应类型集合中的局部编号。`TerrainEditing` 借用调用方提供的 `Scheduler`，调度器的使用期限由调用方管理。

| 要素 | HeightField | VoxelField |
| --- | --- | --- |
| 样本含义 | `H(x,z)`，沿局部 +Y 方向的高度，单位为米 | `D(x,y,z)`，负值为实心、零值为表面的有符号标量 |
| 网格定义 | `HeightGrid(Width, Depth, PageSize, SampleSpacing)` | `VoxelGrid(PageSize, SampleSpacing)` |
| 样本范围 | X 为 `[0, Width)`，Z 为 `[0, Depth)` | 按全局整数 X/Y/Z 坐标组织的稀疏区域 |
| 区域表示 | `HeightRegion(MinX, MinZ, Width, Depth)` | `VoxelRegion(MinX, MinY, MinZ, Width, Height, Depth)` |
| 创建时的默认值 | `initialHeight = 0f` | `initialValue = 1f` |
| 每页样本数 | `PageSize × PageSize` | `PageSize × PageSize × PageSize` |
| CPU 读取 | `HeightFieldRead[x,z]` | `VoxelFieldRead[x,y,z]` 与三线性 `Sample(position)` |

区域的最大坐标采用开区间。这里的全局样本坐标以整个字段为参照；`SamplePosition` 将样本坐标乘以 `SampleSpacing`，得到地形局部米制坐标。高度场的编辑和读取区域取与 `Grid.Range` 的交集。体素页坐标采用向下取整，正负样本坐标遵循相同的页面归属；字段初始值是稀疏页面读取的默认值。

源码：[HeightGrid 与 HeightRegion](../../../src/SomeEngine.Resources/Terrain/HeightGrid.cs)、[VoxelGrid 与 VoxelRegion](../../../src/SomeEngine.Resources/Terrain/VoxelGrid.cs)、[高度读取](../../../src/SomeEngine.Resources/Terrain/HeightFieldRead.cs)、[体素读取](../../../src/SomeEngine.Resources/Terrain/VoxelFieldRead.cs)。

## 创建、编辑和读取

以下是高度场调用示例。`8 × 6` 个样本、每页边长 `4`、间距 `1` 米、初始高度 `2` 米和增量 `3` 米均为示例参数。

```csharp
using SomeEngine.Job;
using SomeEngine.Resources;

using var scheduler = new Scheduler();
var terrain = new Terrain(new TerrainEditing(scheduler));
HeightField height = terrain.CreateHeightField(
    "ground", new HeightGrid(8, 6, 4, 1f), initialHeight: 2f);

var region = new HeightRegion(2, 1, 4, 3);
TerrainEdit edit = height.EditCpu(new HeightDeltaBrush(region, 3f));
await edit.WaitAsync();
HeightFieldRead data = await height.ReadCpuAsync(height.Grid.Range);

float changedHeight = data[2, 1]; // 5 米
float surroundingHeight = data[1, 1]; // 2 米
ulong readRevision = data.Revision; // edit.Revision
```

`HeightDeltaBrush` 在区域内逐样本增加 `Delta`。其 `ReadBorder` 为零，来源为本次编辑固定的输入版本。源码：[高度笔刷](../../../src/SomeEngine.Resources/Terrain/HeightBrushes.cs)。

以下是体素场调用示例。每页边长 `4`、间距 `1` 米、初始标量 `2`、球心 `(-1,0,0)`、半径 `2` 米和中心增量 `-4` 均为示例参数。

```csharp
using System.Numerics;
using SomeEngine.Job;
using SomeEngine.Resources;

using var scheduler = new Scheduler();
var terrain = new Terrain(new TerrainEditing(scheduler));
VoxelField voxel = terrain.CreateVoxelField(
    "cave", new VoxelGrid(4, 1f), initialValue: 2f);

var brush = new VoxelSphereDeltaBrush(
    voxel.Grid, new Vector3(-1, 0, 0), 2f, -4f);
TerrainEdit edit = voxel.EditCpu(brush);
await edit.WaitAsync();
VoxelFieldRead data = await voxel.ReadCpuAsync(brush.Range);

float center = data[-1, 0, 0]; // -2，实心
float surface = data[0, 0, 0]; // 0，表面
float between = data.Sample(new Vector3(-0.5f, 0f, 0f)); // -1
```

`VoxelSphereDeltaBrush` 从网格、球心和半径求出样本区域，按 `Delta × max(0, 1 - distance / Radius)` 修改标量。球心和半径采用地形局部米制坐标。`Sample(position)` 先用字段间距换算样本坐标，再在所在单元的八个样本之间做三线性插值；调用所用区域包含这些样本。源码：[体素笔刷](../../../src/SomeEngine.Resources/Terrain/VoxelBrushes.cs)、[三线性采样](../../../src/SomeEngine.Resources/Terrain/VoxelFieldRead.cs)。

## 提交版本与字段完成

创建字段和提交编辑都会推进 `Terrain.Revision`。字段的 `Revision` 表示它最近提交的版本，读取结果的 `Revision` 表示该读取所捕获的字段版本。

`TerrainEdit` 提供本次编辑的 `Revision`、`JobHandle Completion` 和 `WaitAsync()`。`Completion` 对应本次字段样本工作完成，可作为同一调度器中后续任务的依赖；`WaitAsync()` 将任务完成或失败结果传给调用方。源码：[TerrainEdit](../../../src/SomeEngine.Resources/Terrain/TerrainEdit.cs)、[Job 完成观察器](../../../src/SomeEngine.Job/JobSystem.cs)。

同一字段的 `EditCpu` 自动依赖前一次编辑，随后一笔编辑使用前一笔的样本结果。可选 `dependency` 参数把外部 `JobHandle` 合入本次输入依赖。一次编辑按页提交任务，各页写入各自的新数组，输入数组通过 `JobResourceAccess.Read`、输出数组通过 `JobResourceAccess.Write` 参加调度。源码：[高度编辑](../../../src/SomeEngine.Resources/Terrain/TerrainEditing.cs)、[体素编辑](../../../src/SomeEngine.Resources/Terrain/TerrainEditing.Voxels.cs)。

`ReadCpuAsync` 在调用时捕获字段数据，再等待该版本完成。下面沿用高度场示例：读请求先捕获高度 `5` 的版本，随后提交增量 `2`；这些数值仍是示例参数。

```csharp
ValueTask<HeightFieldRead> earlierRequest = height.ReadCpuAsync(height.Grid.Range);
TerrainEdit laterEdit = height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 2f));

HeightFieldRead earlier = await earlierRequest;
await laterEdit.WaitAsync();
HeightFieldRead current = await height.ReadCpuAsync(height.Grid.Range);

float earlierHeight = earlier[2, 1]; // 5
float currentHeight = current[2, 1]; // 7
```

体素场使用相同的调用时捕获与等待顺序。两个入口分别见 [HeightField.ReadCpuAsync](../../../src/SomeEngine.Resources/Terrain/HeightField.cs) 和 [VoxelField.ReadCpuAsync](../../../src/SomeEngine.Resources/Terrain/VoxelField.cs)。

## 共享与复制

多个引用指向同一个 `Terrain` 时，共同观察该对象后续提交的字段数据。`Duplicate()` 创建另一组字段对象，并保留复制时的字段版本；两个 Terrain 最初共享页面数据，各自的后续编辑建立自己的页面值。已有 CPU 读取对象继续持有原版本的数据。

下面是独立的复制示例。网格尺寸、初始高度 `5`、原对象增量 `2` 与副本增量 `10` 均为示例参数。

```csharp
using SomeEngine.Job;
using SomeEngine.Resources;

using var scheduler = new Scheduler();
var terrain = new Terrain(new TerrainEditing(scheduler));
HeightField height = terrain.CreateHeightField("ground", new HeightGrid(8, 6, 4), 5f);
HeightFieldRead earlier = await height.ReadCpuAsync(height.Grid.Range);
Terrain shared = terrain;
Terrain copy = terrain.Duplicate();

await height.EditCpu(new HeightDeltaBrush(height.Grid.Range, 2f)).WaitAsync();
HeightField copiedHeight = copy.HeightFields[0];
await copiedHeight.EditCpu(new HeightDeltaBrush(copiedHeight.Grid.Range, 10f)).WaitAsync();

HeightFieldRead sharedData = await shared.HeightFields[0].ReadCpuAsync(height.Grid.Range);
HeightFieldRead copiedData = await copiedHeight.ReadCpuAsync(copiedHeight.Grid.Range);
float sharedHeight = sharedData[1, 1]; // 7
float copiedValue = copiedData[1, 1]; // 15
float earlierHeight = earlier[1, 1]; // 5
```

`Duplicate()` 同时保留高度场和体素场，副本继续借用同一个 `TerrainEditing`。字段数据和读取对象通过普通托管引用保留页面。源码：[Terrain.Duplicate](../../../src/SomeEngine.Resources/Terrain/Terrain.cs)、[HeightFieldRead](../../../src/SomeEngine.Resources/Terrain/HeightFieldRead.cs)、[VoxelFieldRead](../../../src/SomeEngine.Resources/Terrain/VoxelFieldRead.cs)。

## 自定义 CPU 笔刷与邻域

CPU 笔刷使用实现相应接口的值类型。`EditCpu` 按值复制笔刷参数，`Apply` 从固定输入版本计算一个新样本。

| 成员 | IHeightBrushCpu | IVoxelBrushCpu |
| --- | --- | --- |
| 写入区域 | `HeightRegion Range` | `VoxelRegion Range` |
| 输入邻域 | `int ReadBorder`，单位为样本 | `int ReadBorder`，单位为样本 |
| 样本计算 | `float Apply(HeightFieldRead source, int x, int z)` | `float Apply(VoxelFieldRead source, int x, int y, int z)` |

编辑服务按每页实际写入区域扩展 `ReadBorder`，向笔刷提供该输入区域。高度场输入区域继续与字段范围取交集。邻域内各样本来自同一个编辑前版本，输出写入新页面。接口见[高度笔刷](../../../src/SomeEngine.Resources/Terrain/HeightBrushes.cs)与[体素笔刷](../../../src/SomeEngine.Resources/Terrain/VoxelBrushes.cs)。

内置平滑、压平及读取视图的梯度入口见[地形邻域笔刷](Terrain-Brushes.md)。它们使用同一字段编辑与页面读取路径。

## 行为检查入口

- [HeightFieldTests](../../../tests/SomeEngine.Resources.Tests/HeightFieldTests.cs)：区域增量、提交顺序、固定版本读取、共享复制与跨页邻域读取。
- [VoxelFieldTests](../../../tests/SomeEngine.Resources.Tests/VoxelFieldTests.cs)：球形衰减、稀疏区域读取、三线性采样、连续编辑与复制后的独立数据。

返回[地形导航](../Terrain.md)。
