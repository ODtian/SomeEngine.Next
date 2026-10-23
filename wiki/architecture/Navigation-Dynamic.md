# 动态来源、局部重建与路径修正

`NavigationSourceSystem` 收集完成的几何与实际变换，`NavigationBuildSystem` 按输入版本重建受影响块，`NavigationApplySystem` 将匹配的详细块、入口图和连接一起安装到 `NavigationScene`。路径阶段沿同一组具体搜索器接口继续执行。

## 来源与固定输入

`NavigationSource(Geometry, Version)` 表示支持导航构建的几何实体。`NavigationObstacle(Geometry, Participation, Version)` 表示障碍实体，默认 `Obstruction` 将有向闭合三角形几何转为实心占用；`SupportAndObstruction` 同时保留顶面的可行走法线。两个组件的 enable 状态控制参与。几何通过普通 `NavigationGeometry` 持有固定数组；替换几何、输入版本或实际变换都会进入变化判断。

`INavigationTransformReader<TTransform>.Read` 从应用的实际变换组件返回世界矩阵。`NavigationSourceSystem<TTransform,TReader>.Schedule(transformReady)` 等待该变换生产者后，使用生成式 ECS Job 读取来源与障碍。各分片收集当前几何引用、矩阵、版本和世界范围；后续合并 Job 将当前实体集合与上次集合比较。加入的来源贡献新范围，移除的来源贡献旧范围，移动或替换贡献两者并集。范围按完整轮廓旋转半径与栅格边界采样距离扩张，再映射到原区域坐标中的 Tile。

`NavigationRegionSources` 持有一个世界坐标区域的 Scene、区域 ID、范围、规格、策略、构建设置和命名端点。`SetProfile`、`SetPolicy`、`SetBuildSettings` 与 `SetPorts` 形成新的设置引用，下一次来源收集标记整个区域。规格和策略仍与该区域图的使用者保持同一变体。

`NavigationSourceInput` 保存本次固定 `NavigationBuildInput`、设置引用、整体输入版本和每块输入版本。`NavigationSourceChanges.ChangedTiles` 给出本轮变化范围。一次收集在提交时保持设置引用，在变换就绪后取得实际几何与矩阵；已经提交的构建继续持有自己取得的输入。

## 局部计算

`NavigationSurfaceBuilder.Schedule(input, coordinates, ...)` 按选中坐标生成 Surface 块，区域原点始终来自完整输入范围。动态构建将新 Surface 块和已完成的其它 Surface 块组成一致表面，再沿 [Recast 构建流程](https://recastnav.com/group__recast.html)生成轮廓、凸多边形与详细高度。

`NavigationBuildSystem.Schedule(changes, scheduler)` 将本次每块输入版本与上次已安装构建比较，因此多轮尚待安装的变化会一起进入当前计算。每块 Recast Job 写自己的输出；相邻块对 Job 从新旧两侧的实际完成值生成边界连接。两侧版本都延续的连接保持原值。块 Mesh 的 `ContentVersion` 来自对应输入版本，未变化块保持原 Mesh 引用。

入口图根据完整邻接建立新的索引。详细 Mesh 和入口位置都延续的块复用已有局部入口成本；变化块和入口发生变化的邻块分别通过 Job 重算局部路线。结果 `NavigationRegionUpdate` 给出 `Input`、`RebuiltTiles`、`Surface` 与完整 `Mesh`。保留的构建数据用于下一轮局部计算。

## 安装与流送

`NavigationApplySystem.Schedule(update, scheduler)` 在区域与 Scene 的写访问中核对当前输入和设置引用。匹配结果一次安装完整区域图、对应详细块及连接版本；当前其它区域和未变化块的实际引用继续沿用。已有区域沿用当前驻留 Tile 集合，新的区域取得初始完整集合。

`NavigationSceneContent.Version` 表示当前引用绑定；`ContentVersion` 表示导航内容更新；`ResidenceVersion` 表示详细 Tile 身份集合的变化。内容替换和驻留变动各自推进相应版本。普通路径分段和已经提交的任务保留实际旧数组，完成结果交付后的查询取得当前值。

已安装区域通过 `NavigationRegionSources.CreateTileSource()` 提供当前完成块的独立来源引用。应用将它交给 `NavigationStreaming.SetSource(region, source)`，后续读取取得该次完成内容。既有读取持有其原来源直到完成。流送接入按当前图的块内容版本匹配数据；当前完成值在访问准备期间变化时，接入重新取得对应目录数组。

## 请求续行

`NavigationSceneContent` 为块内容版本和连接版本建立索引，`AreDependenciesCurrent(path)` 检查路径实际记录的 `Dependencies` 与 `ConnectionDependencies`。`SceneNavigationSearch.Update` 在内容版本变化时使用这些依赖：相关内容改变后，从角色当前实际位置建立新的具体搜索过程，并以 `ResetPathProgress` 交付新的拐点进度。其它已完成路径继续保持原结果引用。

等待详细块的请求按已选粗路线的块与连接版本判断内容变化；粗路线形成前使用新的完成图继续搜索。驻留变化通过[流送续行入口](Navigation-Streaming.md)取得后续详细块。运动建议仍由业务消费 `NavigationSteering`，实际位置、速度和朝向继续进入 `ScheduleProgress`。

以下是应用调用顺序的例子，几何实体、变换组件和区域配置由游戏提供：

```csharp
var region = new NavigationRegionSources(scene, regionId, bounds,
    profile, policy, surfaceSettings, meshSettings, ports);
var sources = new NavigationSourceSystem<Transform, TransformReader>(world, region, scheduler);

NavigationBuild<NavigationSourceChanges> changes = sources.Schedule(transformReady);
NavigationBuild<NavigationRegionUpdate> update = NavigationBuildSystem.Schedule(changes, scheduler);
JobHandle applied = NavigationApplySystem.Schedule(update, scheduler);
NavigationStep step = navigation.Schedule(deltaTime, expansionBudget, applied);
JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, step.Steering);
navigation.ScheduleProgress(moved);
```

实现入口：[来源与输入](../../src/SomeEngine.Navigation/NavigationSources.cs)、[构建和安装](../../src/SomeEngine.Navigation/NavigationBuildSystems.cs)、[搜索续行](../../src/SomeEngine.Navigation/SceneNavigationSearch.cs)。公共行为入口：[NavigationDynamicTests](../../tests/SomeEngine.Navigation.Tests/NavigationDynamicTests.cs)，其中地板、障碍盒和远处角色是输入例子。
