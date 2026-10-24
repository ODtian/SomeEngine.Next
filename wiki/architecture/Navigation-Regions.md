# 区域入口图与场景放置

`NavigationRegionBuilder` 为多边形网格建立入口图，`NavigationSceneBuilder` 将多个带变换的区域和显式连接组成普通 `NavigationScene`。场景路径通过 `NavigationSystems<NavigationScene,SceneNavigationSearch,SceneSearchState,TMotion,TReader>` 进入共同引导、运动消费和到达进度。

## 匹配的粗图与详细块

`NavigationMesh.Tiles` 保存每块独立的详细 `NavigationMesh`。烘焙入口直接带出各块完成数组；程序化网格由区域构建入口按 `NavigationPolygon.Tile` 分组并生成独立数组。每块多边形索引和内部入口都相对该块。

`NavigationRegionBuilder.Schedule(mesh, profile, policy, ports, dependency, scheduler)` 返回 `NavigationBuild<NavigationRegionBuildState>`。完成后的 `Mesh` 同时持有这些详细块和匹配的 `RegionGraph`。`NavigationRegionPort` 定义供跨区域连接使用的命名端点。

`NavigationRegionGraph` 持有块目录、入口、图边与命名端点数组。目录给出空间范围、内容版本及入口范围。入口保留所属块、多边形、位置与边界段。图边记录真实局部路线或有向跨块连接的累计成本。图本身独立于详细数组的驻留集合。

该分层采用 [HPA* 的入口图思路](https://webdocs.cs.ualberta.ca/~mmueller/ps/2004/hpastar.pdf)。自动跨块入口来自原网格的定向 portal；每侧位置按相应详细表面采样。构建器对同块入口有序对运行局部 `PolygonNavigationSearch`，将完成路径的成本保存为图边权重。每块的局部成本计算由独立 Job 负责，完成后组装图数组。原始跨块邻接继续使用[网格烘焙](Navigation-Baking.md)中的相邻块对 Job。

图与详细路线使用相同规格和策略变体。局部成本遵循多边形入口图的区域距离权重、入口附加成本和动作成本；Funnel 提供其所选走廊的消费拐点。

## 放置区域与完成值

`NavigationRegionPlacement` 给出场景内区域身份、来源 Mesh 和 LocalToWorld。`NavigationSceneBuilder` 将顶点、详细高度、入口端点和命名端点转换到世界坐标，并依据实际行进方向排列 portal 左右端点。随后在变换后的详细网格上计算匹配入口成本。

`NavigationSceneRegion` 保存区域身份、放置变换与世界坐标入口图。`NavigationSceneTile` 保存区域身份、本地 Tile 坐标和世界坐标详细网格。`NavigationSceneConnection` 用两侧 Region/Port 身份定义连接，同时携带方向、附加成本、能力、动作及内容版本。不同放置可以复用同一本地 Tile 坐标，联合身份由 `NavigationSceneTileId(Region, Tile)` 表达。

`NavigationSceneContent` 是完成的区域、连接和驻留块引用集合。`NavigationScene.Content` 返回当前完成值；`ScheduleApply` 在指定 Scheduler 上写入新的完整引用集合，并同时推进其绑定版本。`INavigationSpace.GetVersion` 由 `PathNavigationResources` 转发给组件资源准备流程。Collect 读取 Scene 引用持有对象及当前完成值的实际数组；Apply 写入同一持有对象。任务授予全部访问后，既有资源版本检查确认当前引用集合。

## 请求、路径与数据持有

`SceneSearchState` 为每个请求分别保存端点连接搜索、粗图队列、前驱边和详细搜索。预算分别推进端点局部 A*、粗图 A* 与选中路线的详细 A*。粗图边保持其真实累计成本，详细阶段重建对应块内走廊并消费已有 Funnel。

`NavigationPath.RegionRoute` 给出选中入口的区域、Tile、入口身份、世界位置以及粗路线成本。`Sections` 给出详细块引用及其在公共 `Corridor` 数组中的范围；该范围内的多边形索引相对分段持有的 Mesh。`Corners` 使用连续世界位置，`Cost` 累计本次详细搜索与连接成本。

`Dependencies` 通过 Region、Tile 和块内容版本标记实际使用的数据；`ConnectionDependencies` 记录采用的场景连接及内容版本。每个详细分段保持自己的数组引用，路径高度采样从这些已取得分段读取。Scene 目录持有当前驻留引用，粗图和已取得路径各自保持所需数据。

以下是调用方式的例子，资源、端点和变换由游戏提供：

```csharp
NavigationMesh region = NavigationRegionBuilder.Schedule(mesh, profile, policy,
    ports, scheduler: scheduler).Complete().Mesh;
NavigationScene scene = NavigationSceneBuilder.Schedule(
    placements, connections, profile, policy, scheduler: scheduler).Complete().Scene;

var navigation = new NavigationSystems<NavigationScene, SceneNavigationSearch,
    SceneSearchState, Movement, MovementReader>(world);
navigation.AddAgent(entity, scene, profile, policy,
    new NavigationGoal(destination, arrivalRange: 0.1f), desiredSpeed: 2f);
```

阶段完成句柄与实际运动反馈继续使用[目标、路径与实际运动](Navigation-Paths.md)中的 `Schedule`、`Steering` 和 `ScheduleProgress`。

粗图先就绪、详细前段分批取得的请求与独立后台组见[导航流送](Navigation-Streaming.md)。

来源、实际变换与规格变化后的块版本、局部构建和依赖修正见[动态导航](Navigation-Dynamic.md)。

实现入口：[入口图](../../src/SomeEngine.Navigation/NavigationRegionGraph.cs)、[局部成本构建](../../src/SomeEngine.Navigation/NavigationRegionBuilder.cs)、[场景与完成值](../../src/SomeEngine.Navigation/NavigationScene.cs)、[放置构建](../../src/SomeEngine.Navigation/NavigationSceneBuilder.cs)、[场景搜索](../../src/SomeEngine.Navigation/SceneNavigationSearch.cs)。公共行为入口：[NavigationSceneTests](../../tests/SomeEngine.Navigation.Tests/NavigationSceneTests.cs)。
