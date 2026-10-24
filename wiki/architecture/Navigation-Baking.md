# 按角色规格烘焙导航网格

`NavigationMeshBuilder` 从共享 `NavigationSurface` 派生与 `NavigationProfile`、`NavigationPolicy` 对应的普通导航网格。几何入口先调用同一 `NavigationSurfaceBuilder`，导入器也调用这两个计算入口。

本文维护已实现的构建算法、公共入口与结果数据职责。源表面见[导航几何与多层表面](Navigation-Surfaces.md)，结果消费见[目标、路径与实际运动](Navigation-Paths.md)，变体保存见[导航规格、策略与网格资产](Assets/Asset-Navigation.md)。

## 通行与 Recast 阶段

每块将源占据区间转换成 Recast 高度场，依据角色坡度、支撑高度到上方占据区间的净空，以及策略的可用区域完成筛选。Recast 继续执行低障碍、边缘与净空过滤、紧凑高度场连接、单调区域划分、轮廓提取、凸多边形合并和详细高度采样。计算使用 [DotRecast.Recast 2026.3.1](https://github.com/ikpil/DotRecast/tree/2026.3.1/src/DotRecast.Recast)，依赖通过 NuGet 引入。

普通网格允许角色随路线改变朝向，因此 `NavigationSurfaceTraversal.RasterizeFootprintSweep` 使用轮廓绕原点的外接圆覆盖全部朝向，并覆盖参考点在完整方格内的平移。圆扫掠范围相交的单元共同参与筛选；每个覆盖单元都需要可用支撑、可跨越的高度差和足够的共同净空。该职责对应 [SE(2) Navigation Mesh 的普通通行区与朝向层定义](https://arxiv.org/html/2607.01454v1#S4.SS2)。`RasterizeFootprint` 提供指定轮廓在零朝向（+Z）的单元覆盖；连续通道构建与查询见[朝向导航网格](Heading-Navigation.md)。

`NavigationSurfaceTraversal.IsWalkable` 使用源表面的法线、支撑高度、上方占据区间与区域策略。`CanTraverse` 比较两个支撑的高度差及开放高度区间交集。源面积分类映射为 Recast 的正面积编号，在多边形输出时恢复原分类。详细高度样本回到源表面的相应高度层，`NavigationMesh.TrySampleHeight` 与角色引导消费这些三角形。

## Job 与完整结果

`Schedule(input, profile, policy, settings, dependency, scheduler)` 接收几何构建输入；`Schedule(surface, profile, policy, settings, dependency, scheduler)` 接收已取得表面。两者返回 `NavigationBuild<NavigationMeshBuildState>`。`Completion` 完成后，`State.Mesh`、`State.Surface` 与 `State.Tiles` 可供调用方使用。

几何入口为轮廓扫掠和邻接保留边界采样；表面入口同时读取相邻块的列。每块对应一个构建 Job，每对相邻块对应一个入口连接 Job，最后一个创建 Job 合并完整结果。跨块连接沿重合边逐采样单元确认台阶与共同净空，连续通行区间组成双向入口。数组访问绑定调用方的 Scheduler；显式依赖决定本次计算的输入何时就绪。

`NavigationMeshBuildState.Mesh` 持有完成的位置、多边形顶点索引、多边形、双向入口、详细高度位置和三角形。每个 `NavigationPolygon.Tile` 保存原始块坐标。`Tiles` 中的 `NavigationMeshTileRange` 给出每块在最终网格中的多边形起点与数量，各多边形继续给出详细高度与入口范围。跨块入口的目标索引指向同一网格，端点与详细高度使用同一世界坐标。

完成 Mesh 的 `Tiles` 属性另行持有各块独立的详细网格数组。区域构建与场景放置直接使用这些块，接口见[区域入口图与场景放置](Navigation-Regions.md)。

以下是程序化调用的例子；几何输入、规格、策略与前置依赖由业务提供：

```csharp
var build = NavigationMeshBuilder.Schedule(input, profile, policy,
    new NavigationMeshSettings { ContentVersion = version }, geometryReady, scheduler);
NavigationMeshBuildState result = build.Complete();
NavigationMesh mesh = result.Mesh;
ReadOnlyMemory<NavigationMeshTileRange> tiles = result.Tiles;
```

`MaximumContourError` 的单位为水平采样单元；`MaximumEdgeLength`、`DetailSampleDistance` 与 `DetailMaximumError` 的单位为米。`MinimumRegionCells`、`MergeRegionCells` 表示区域面积的单元数，`MaximumPolygonVertices` 限制凸多边形顶点数。`ContentVersion` 进入完成资源与资产内容。

## 导入选择

`NavigationMeshImportSource` 持有固定的 `NavigationBuildInput`、`NavigationMeshImportVariant` 数组和可选 Scheduler。每个变体提供 `NavigationMeshSelection`、实际 Profile 与 Policy。Selection 由 Profile 资产身份和构建变体名组成。

生成的 `EngineAssetCapabilities` 提供 `NavigationMeshImporter` 能力。调用方从能力描述绑定并启用该导入器，经 `ImportContext.GetAssetId` 取得身份，直接创建 `NavigationAsset`，再通过 `AddAsset` 交付输入、资产和输出路径。导入器共享一次表面构建，提交每种规格的网格 Job，将完整结果安装到一个 `NavigationMeshBlock`，并通过 `ImportOutput` 交付。设置使用 `NavigationMeshSettings` 的生成 JSON 编解码器。网格数组的保存与读取通过现有 `NavigationArrayData` Job 完成。

实现：[设置与结果](../../src/SomeEngine.Navigation/NavigationMeshBuild.cs)、[调度与入口连接](../../src/SomeEngine.Navigation/NavigationMeshBuilder.cs)、[Recast 转换](../../src/SomeEngine.Navigation/NavigationRecast.cs)、[共同通行筛选](../../src/SomeEngine.Navigation/NavigationSurfaceTraversal.cs)、[导入器](../../src/SomeEngine.Assets.Importers/Importers/NavigationMeshImporter.cs)。公共行为入口：[NavigationBakeTests](../../tests/SomeEngine.Assets.Tests/NavigationBakeTests.cs)。
