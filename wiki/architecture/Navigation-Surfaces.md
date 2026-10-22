# 导航几何与多层表面

`SomeEngine.Navigation` 持有导航几何、表面与构建对象，依赖 ECS.Systems 的 Job 调度能力。`SomeEngine.Assets` 引用导航项目，提供 MeshAsset 中的 NavigationGeometryBlock 与 NavigationAsset 中的 NavigationSurfaceBlock。

## 几何输入

`NavigationGeometry` 保存位置、带面积分类的三角形和局部包围范围。位置与拓扑通过 `ReadOnlyMemory` 共享；提供数组的调用方在资源使用期保持数组内容只读。

`ReadTriangles<TSource, TReader>` 通过 `INavigationTriangleReader<TSource>` 读取已有碰撞或三角形来源。`ReadHeightfield<TSource, TReader>` 通过 `INavigationHeightfieldReader<TSource>` 读取地形网格顶点及每个四边形的面积分类。读取器使用静态泛型调用，输出统一的导航几何。

`NavigationBuildInput` 保存固定来源数组、每个来源的几何引用、放置矩阵和版本，以及构建区域和 `NavigationSurfaceSettings`。放置沿 +Y 向上的坐标约定，镜像矩阵按行列式修正三角形绕序。

## 分块构建

`NavigationSurfaceBuilder.Schedule(input, dependency)` 返回 `NavigationBuild<NavigationSurfaceBuildState>`。`Completion` 表达整个构建完成，`Complete().Surface` 取得结果；`State.Input` 保留该次输入。工作区和块结果由 State 持有，最后一个创建 Job 组装完整表面。

每块作为独立 Job 处理，读取固定几何数组，写入自己的块结果和工作区数组范围。显式前置句柄决定开始顺序。构建将三角形裁剪到 XZ 单元，沿高度形成占据区间，再合并相交区间。流程依据 [Recast 高度场与栅格化接口](https://recastnav.com/group__recast.html)。

`CellSize` 表示水平采样间距，`CellHeight` 表示占据区间的量化间距，`TileSize` 表示每块内部单元边长。每块四周保留 `ceil(BorderSize / CellSize) + 1` 个采样单元，分别承接调用方的轮廓范围与相邻单元。`InteriorBounds` 表示该块负责的区域，`Origin`、`Width`、`Depth` 包含边界采样区域。

每个 `NavigationSurfaceSpan` 保存占据区间、区间顶部支撑高度、表面法线与面积分类。支撑高度采用三角形在该单元内的最高点；多层区间按高度排列。完成后的列和区间数组交给 `NavigationSurfaceTile` 只读共享，构建释放临时列表引用。

## 查询和保存

`NavigationSurface.GetColumn` 取得世界 XZ 位置的多层区间。`TryGetSupport` 按调用方给出的高度与垂直距离选择最近的向上表面，返回位置、法线、面积分类与上方占据区间的最低高度。`NavigationSurfaceTile.GetColumn` 同时向后续表示构建器提供边界单元的查询。

`NavigationGeometryBlock.SetGeometry` 接收几何，`CreateAsync` 显式取得位置与拓扑数据。`NavigationSurfaceBlock.SetSurface` 接收完整表面；目录记录每块范围和柱区间数据键。`ReadTileAsync` 取得单块，`CreateAsync` 取得整个表面。数组编码与解码通过 Job 执行；已取得资源通过普通引用保持数组有效。

以下是调用方式的例子，几何、区域与设置由业务提供：

```csharp
var build = NavigationSurfaceBuilder.Schedule(input, geometryReady);
NavigationSurface surface = build.Complete().Surface;
using var block = new NavigationSurfaceBlock();
block.SetSurface(surface);
using var asset = new NavigationAsset { Name = "walkways" };
asset.SetBlock(block);
await asset.SaveAsync(path);

using var reopened = await NavigationAsset.OpenAsync(path);
NavigationSurface restored = await (await reopened.GetBlockAsync<NavigationSurfaceBlock>()).CreateAsync();
restored.TryGetSupport(position, maximumVerticalDistance, out NavigationSupport support);
```

实现入口：[几何](../../src/SomeEngine.Navigation/NavigationGeometry.cs)、[读取器](../../src/SomeEngine.Navigation/NavigationGeometryReaders.cs)、[构建](../../src/SomeEngine.Navigation/NavigationSurfaceBuilder.cs)、[表面](../../src/SomeEngine.Navigation/NavigationSurface.cs)、[几何 Block](../../src/SomeEngine.Assets/Schema/NavigationGeometryBlock.cs)、[表面 Block](../../src/SomeEngine.Assets/Schema/NavigationSurfaceBlock.cs)。公共行为检查位于 [SurfaceBuildTests](../../tests/SomeEngine.Navigation.Tests/SurfaceBuildTests.cs) 与 [NavigationSurfaceAssetTests](../../tests/SomeEngine.Assets.Tests/NavigationSurfaceAssetTests.cs)。
