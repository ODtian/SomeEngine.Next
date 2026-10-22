# 地形邻域笔刷

本页维护已实现的 CPU 算法与公共接口（Wiki 类别 1、2）。源码入口为 [HeightNeighborhoodBrushes](../../../src/SomeEngine.Resources/Terrain/HeightNeighborhoodBrushes.cs)、[VoxelNeighborhoodBrushes](../../../src/SomeEngine.Resources/Terrain/VoxelNeighborhoodBrushes.cs)、[HeightFieldRead](../../../src/SomeEngine.Resources/Terrain/HeightFieldRead.cs) 与 [VoxelFieldRead](../../../src/SomeEngine.Resources/Terrain/VoxelFieldRead.cs)。字段版本、页访问与提交入口由 [CPU 字段](Terrain-Fields.md)维护。

## 坐标与衰减

平滑和压平使用相同的径向权重：`weight = Strength × max(0, 1 - distance / Radius)`，并计算 `lerp(source, target, weight)`。`Strength = 0` 保留原值，`Strength = 1` 在笔刷中心采用完整目标值。半径边界的权重为零。

| 参数 | 高度笔刷 | 体素笔刷 |
| --- | --- | --- |
| 网格 | `HeightGrid` | `VoxelGrid` |
| 中心 | `Vector2 Center`，地形局部 X/Z 米制坐标 | `Vector3 Center`，地形局部 X/Y/Z 米制坐标 |
| 作用半径 | `float Radius`，单位为米 | `float Radius`，单位为米 |
| 强度 | `float Strength`，默认 1 | `float Strength`，默认 1 |
| 平滑邻域 | `int NeighborRadius`，单位为样本 | `int NeighborRadius`，单位为样本 |
| 压平目标 | `Plane TargetPlane`，地形局部平面 | `Plane TargetPlane`，地形局部平面 |

笔刷构造时从中心、半径和网格间距取得全局样本范围。`EditCpu` 固定输入版本并按页调度；各页读到同一次提交前的邻域样本，输出写入新页。高度场的范围与字段范围取交集，体素场沿用稀疏字段的全局整数坐标。

## 平滑

`HeightSmoothBrush(grid, center, radius, neighborRadius, strength)` 以边长 `2 × NeighborRadius + 1` 的正方形邻域均值为目标。高度场边界处使用域内样本及其实际数量。

`VoxelSmoothBrush(grid, center, radius, neighborRadius, strength)` 以相同边长的立方体邻域均值为目标。稀疏区域的样本值来自字段的 `InitialValue`。

两者的 `ReadBorder` 均为 `NeighborRadius`。字段服务一次捕获所需邻域；平滑过程中每个样本使用这份固定输入。

## 压平

`HeightFlattenBrush(grid, center, radius, plane, strength)` 和 `VoxelFlattenBrush(grid, center, radius, plane, strength)` 在构造时归一化目标平面。平面采用 `dot(normal, position) + D = 0`。

高度压平取指定 X/Z 处的平面高度：`-(normal.X × x + normal.Z × z + D) / normal.Y`。调用方提供可表达高度的平面。体素压平取 `dot(normal, position) + D` 为目标标量，平面法线决定实心侧与空侧。两者的 `ReadBorder` 为零，当前样本与固定目标平面共同决定输出。

## 梯度与自定义笔刷

`HeightFieldRead.GetGradient(x, z)` 返回高度沿局部 X/Z 的导数；`VoxelFieldRead.GetGradient(x, y, z)` 返回标量沿局部 X/Y/Z 的导数。计算使用该读取版本的相邻样本，并除以实际米制采样距离。高度场边界使用域内相邻样本，体素场使用两个方向各一个样本的中心差分。

调用方的读取范围包含这些相邻样本。自定义笔刷通过 `ReadBorder = 1` 声明该邻域，再从传入的固定版本视图调用 `GetGradient`。下面是按斜率增加高度的自定义笔刷示例；增加量的选择属于调用方算法。

```csharp
readonly record struct SlopeHeightBrush(HeightRegion Range, float Amount) : IHeightBrushCpu
{
    public int ReadBorder => 1;
    public float Apply(HeightFieldRead source, int x, int z) =>
        source[x, z] + Amount * source.GetGradient(x, z).Length();
}
```

## 公开应用示例

[HeightTerrainExample](../../../samples/DefaultGame/HeightTerrainExample.cs) 与 [VoxelTerrainExample](../../../samples/DefaultGame/VoxelTerrainExample.cs) 提供 `Smooth`、`Flatten` 方法，返回可等待的 `TerrainEdit`。两类方法均复用对应字段的 `EditCpu`；已安装地形功能的应用按范围更新块几何，采样通过字段的 `ReadCpuAsync` 取得。

下面的圆心 `(2,2)`、三米半径、一个样本邻域和一米目标高度都是高度示例参数。

```csharp
TerrainEdit smooth = ground.Smooth(new Vector2(2, 2), 3f, 1);
await smooth.WaitAsync();
HeightFieldRead afterSmooth = await ground.Heights.ReadCpuAsync(ground.Heights.Grid.Range);

TerrainEdit flatten = ground.Flatten(new Vector2(2, 2), 3f, new Plane(Vector3.UnitY, -1f));
await flatten.WaitAsync();
HeightFieldRead afterFlatten = await ground.Heights.ReadCpuAsync(ground.Heights.Grid.Range);
```

[TerrainBrushTests](../../../tests/SomeEngine.Resources.Tests/TerrainBrushTests.cs) 通过公开字段检查全局跨页参考、米制衰减和自定义梯度笔刷；[TerrainBrushRenderingTests](../../../tests/SomeEngine.Runtime.Tests/TerrainBrushRenderingTests.cs) 通过应用入口同时读取修改后的样本和捕获表面画面。

返回[地形导航](../Terrain.md)。
