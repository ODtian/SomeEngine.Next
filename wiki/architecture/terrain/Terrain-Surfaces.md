# 地形表面组合

本页维护已实现的表面算法、数据职责与公开调用方法。源码入口为 [TerrainSurface](../../../src/SomeEngine.Resources/Terrain/TerrainSurface.cs)、[VoxelSurface](../../../src/SomeEngine.Resources/Terrain/VoxelSurface.cs) 和 [BlendSurface](../../../src/SomeEngine.Resources/Terrain/BlendSurface.cs)。字段存储与编辑见 [CPU 字段](Terrain-Fields.md)，空间块及呈现见 [放置与 CPU 分块](Terrain-Scene.md)。

## 标量源与组合

`Terrain.Surface` 持有普通对象组成的表面根。节点引用同一 Terrain 中的字段，采样坐标为地形局部米制坐标，负值表示实心，零值表示表面。

| 节点 | 保存的数据 | 标量求值 |
| --- | --- | --- |
| `HeightSurface` | `HeightField Field` | `Fh(x,y,z) = y - H(x,z)`；高度在 X/Z 间做双线性插值，并限制到字段边缘 |
| `VoxelSurface` | `VoxelField Field` | `Fv(x,y,z) = D(x,y,z)`；体素在三维单元内做三线性插值 |
| `BlendSurface` | `First`、`Second`、`Weight`，以及可选的 `Region` 和 `FadeDistance` | `F = (1-w) Fa + w Fb`；权重采用下述常量或局部渐变 |

三参数构造函数使用常量 `Weight`。五参数构造函数将第二个源限制在 `Region` 中：令 `d` 为点到包围盒最近内侧面的距离，`t = clamp(d / FadeDistance, 0, 1)`，实际权重为 `Weight × t² × (3 - 2t)`。正的 `FadeDistance` 采用米制距离。区域边界和外部的权重为零，向内经过渐变距离后达到完整权重。

输入和参数由节点保存。调用方通过创建新节点并赋给 `Terrain.Surface` 修改组合；根变更推进 Terrain 版本并触发块更新。`Terrain.Duplicate()` 将节点中的字段引用连接到副本的字段集合。

## 固定版本与读取范围

`ReadCpuAsync(bounds)` 在调用时捕获所有输入字段及其完成依赖，等待对应样本就绪后返回 `TerrainSurfaceRead`。后续编辑写入新的字段页，已有读取继续持有先前版本。`Sample(position)` 求标量，`GetNormal(position, distance)` 通过中心差分取得法线。调用方的 `bounds` 覆盖采样与求导使用的位置。

`ScheduleRead` 在同一次捕获中取得组合源的版本、完成依赖和字段页访问，随后通过已有 Scheduler 执行 `ITerrainSurfaceWork`。高度和体素源的读取范围同时包含插值所需的相邻样本。高度源还通过 `GetHeightRange` 提供列内高度范围，供表面提取限制竖向单元访问。

字段编辑通过 `HeightFieldChange` 或 `VoxelFieldChange` 传播。混合节点沿输入关系判断所引用的字段；空间块根据写入范围、相邻插值单元及算法读取距离选择需要更新的几何。源码为 [TerrainChange](../../../src/SomeEngine.Resources/Terrain/TerrainChange.cs)、[VoxelFieldChange](../../../src/SomeEngine.Resources/Terrain/VoxelFieldChange.cs) 和 [TerrainScene](../../../src/SomeEngine.Terrain/TerrainScene.cs)。

## 从高度生成局部体素

`TerrainSurface.WriteVoxelsCpu(destination, range, dependency)` 捕获源表面，在目标体素网格的样本位置求值，并把结果写入属于同一 Terrain 的目标字段。返回的 `TerrainEdit` 采用字段的版本和完成语义；源页读取与目标页写入共同交给 Scheduler。后续体素编辑使用该写入结果。

以下为调用示例。网格尺寸、页边长、采样间距、区域和渐变距离均为示例参数。

```csharp
using var scheduler = new Scheduler();
var terrain = new Terrain(new TerrainEditing(scheduler));
HeightField heights = terrain.CreateHeightField("ground", new HeightGrid(9, 9, 4));
VoxelField voxels = terrain.CreateVoxelField("detail", new VoxelGrid(4));
var height = new HeightSurface(heights);
var range = new VoxelRegion(0, -8, 0, 9, 17, 9);
TerrainEdit initial = height.WriteVoxelsCpu(voxels, range);
terrain.Surface = new BlendSurface(height, new VoxelSurface(voxels), 1f,
    new TerrainBounds(new(0, -8, 0), new(8, 8, 8)), 2f);

TerrainEdit edit = voxels.EditCpu(new VoxelSphereDeltaBrush(voxels.Grid, new(4, 0, 4), 2f, -1f));
await edit.WaitAsync();
VoxelFieldRead values = await voxels.ReadCpuAsync(range);
TerrainSurfaceRead surface = await terrain.Surface.ReadCpuAsync(new(new(0, -4, 0), new(8, 4, 8)));
```

[BlendTerrainExample](../../../samples/DefaultGame/BlendTerrainExample.cs) 是可编译应用示例：构造时从高度源初始化体素，`Install` 安装地形功能，`AddPlacement` 设置材质与变换，`RaiseDetail` 通过自定义 CPU 笔刷修改体素标量。八米地表、一米采样间距、两米渐变及放置位置属于示例参数。

公开行为入口为 `TerrainSurfaceTests` 的组合读取与初始化检查、`TerrainSceneTests` 的混合源块更新检查，以及 `BlendTerrainRenderingTests` 的应用与图像检查。后者将局部体素编辑与直接写入相同高度平台的场景比较。

同一表面图可由 [GPU 地形几何](Terrain-Gpu-Geometry.md) 捕获并求值，字段页、节点参数和完成依赖使用对应提取版本。`GpuBlendTerrainExample` 展示高度与体素 GPU 编辑后生成混合地形画面的调用。
