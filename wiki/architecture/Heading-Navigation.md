# 朝向导航网格

`HeadingNavigationMesh` 保存凸区域的可行朝向区间、平移连接、原地旋转连接，以及区域边界位置与朝向共同构成的图。它消费与普通网格相同的源表面，依据实际轮廓在各连续朝向通道内生成区域。本文维护当前算法、公共位姿查询与资产数据职责。

## 连续朝向与位置范围

构建方法沿 [SE(2) Navigation Mesh 的通道、区域与图定义](https://arxiv.org/html/2607.01454v1#S5)。`ChannelCount` 将完整朝向周期分成等宽通道；第 `i` 个通道以 `i * 2π / ChannelCount` 为中心，覆盖两侧各半个通道宽度。角度使用 `NavigationHeading`：零朝向沿 +Z，正向转向 +X。

`HeadingNavigationFootprint` 旋转角色实际的完整轮廓，并合并该通道扫掠到的单元。实现以角度样本构造保守覆盖：相邻样本间隔为 `δ`，轮廓绕原点的外接圆半径为 `r`，任意中间位置到最近样本的位移上界为 `2r sin(δ/4)`。角度细分使该上界小于 `min(MaximumSweepError, CellSize/4)`；单元相交矩形按该上界扩张。

Recast 输出的区域覆盖保留单元的完整方格，因此构建掩码还加入参考点在整格内平移的半格余量。`NavigationSurfaceTraversal.RasterizeFootprint` 的默认调用表达固定位置的实际轮廓；构建器通过其 `padding` 参数组合角度位移与参考点平移。普通网格和 Grid 的完整旋转掩码使用同一整格位置约定。

## 区域、连接与图

每个表面支撑先经过坡度、净空、台阶和策略筛选，再逐通道评价整个扫掠轮廓。可行通道集合与原始区域分类共同形成分区标签；同一标签的相连支撑通过 Recast 区域、轮廓与凸多边形阶段生成区域。轮廓简化误差取零以保留通道集合边界，详细高度继续来自源表面的相应高度层。

朝向集合可能形成单格区域。Heading 构建在 Recast 轮廓阶段补入这些区域的四边形轮廓，再共用原多边形与详细高度阶段，使相邻通道集合之间的单格通路进入最终网格。

`Geometry` 以 `NavigationMesh` 数组保存这些凸多边形、详细高度和几何邻接。`Regions` 与 `Geometry.Polygons` 一一对应，给出共享 `Headings` 区间表以及动作和图节点的范围。每个多边形保存原始 Tile 坐标。

`Translations` 连接共享几何入口且拥有相同可行通道的区域，保存目标区域、通道、左右端点与几何 Portal 索引。`Rotations` 连接同一区域中周期相邻的可行通道，并保存带符号的转角；两个通道的连续扫掠共同覆盖整个旋转。

`HeadingNavigationGraph` 在各多边形边界的代表位置和入口位置为每个可行通道建立节点。同一区域、同一通道中的边界节点通过平移边连接；跨区域入口连接同通道节点；同一位置的相邻通道节点通过旋转边连接。`Nodes` 保存区域、通道、位置与出边范围，`Edges` 保存目标节点、动作及跨入口时的 Portal 索引。后续路径求解可直接使用该图与原始动作数据。

## 构建与查询

`HeadingNavigationMeshBuilder.Schedule` 分别接受几何 `NavigationBuildInput` 或已取得 `NavigationSurface`，并接收 Profile、Policy、设置、前置依赖与 Scheduler。按朝向组生成掩码，按块建立高度场，再按块与朝向组评价通行；各块完成分区后，共用普通网格的块间连接与完整数组合并，最后创建朝向图。返回的 `NavigationBuild<HeadingNavigationMeshBuildState>.Completion` 表达整个结果完成。

`GetAllowedHeadings(position)` 返回最近支撑层上包含该点的区域区间并集；`FindRegion(position, heading)` 取得该层允许指定朝向的凸区域；`IsPoseFeasible(position, heading)` 查询该位姿是否属于这些区间；`IsRotationFeasible(position, startHeading, headingChange)` 查询整个带符号旋转范围。`GetTranslationConnections` 和 `GetRotationConnections` 返回当前位置与朝向可用的动作。

以下是调用例子，输入几何、规格、策略和位置由业务提供：

```csharp
var build = HeadingNavigationMeshBuilder.Schedule(input, profile, policy,
    new HeadingNavigationMeshSettings { ChannelCount = 32 }, geometryReady, scheduler);
HeadingNavigationMesh mesh = build.Complete().Mesh;
ReadOnlyMemory<HeadingNavigationInterval> intervals = mesh.GetAllowedHeadings(position);
bool canTurn = mesh.IsRotationFeasible(position, heading, MathF.PI / 2);
HeadingNavigationTranslation[] translations = mesh.GetTranslationConnections(position, heading);
```

## ASA 路径与实际运动

`AsaNavigationSearch` 与 `AsaSearchState` 通过 `NavigationSystems<HeadingNavigationMesh, AsaNavigationSearch, AsaSearchState, TMotion, TReader>` 接入相同目标与预算调度。实现采用 [ASA 的三个阶段](https://arxiv.org/html/2607.01454v1#S5.SS3)：位置/朝向图 A*；合并连续相同区域后的 funnel；在每个精确区域边界交点上重新建立朝向图并执行第二次 A*。共线交点和同位置的相邻区域状态保留各自的可行通道范围。

两次 A* 的 open queue、累计成本与前驱保存在请求工作区中，逐次消费 `expansionBudget`。平移成本使用纵向、横向投影绝对值之和除以 Profile 的 `MaximumSpeed`，再应用区域乘数；本实现的两个轴共用该速度上限。旋转成本为转角绝对值除以 `MaximumAngularSpeed`，入口附加成本与能力筛选继续来自 Policy 与 Portal。启发式累加平面距离下界和最短转角下界。

`Path.Poses` 保存完整位置/朝向序列，`Corners` 保存区域交点，`Sections` 保持详细高度数组。Guidance 沿这些位姿给出速度和受最大角速度限制的朝向。原地旋转等待 `INavigationMotionReader` 的实际朝向达到该位姿，随后推进平移；旋转和平移的完成都由运动反馈决定。`NavigationGoal.Heading` 与 `HeadingTolerance` 同时进入到达进度。

## 朝向区域摘要与显式流送

`HeadingNavigationRegionBuilder.Schedule(mesh, profile, policy)` 在各 Tile 内沿实际 Heading 图求入口间成本。`HeadingNavigationRegionGraph` 的入口保存 Tile、局部区域、位置和朝向通道，边保存同块可行路线及跨块同通道连接；`Portals` 另存完整几何跨块关系。区域成本按各块并行 Job 计算，完成后连同独立 `HeadingNavigationMeshTile` 一起安装。

`HeadingNavigationScene` 保持该区域摘要与当前详细 Tile。`HeadingSceneNavigationSearch` 用 ASA 连接实际起终点与摘要入口，再逐块细化选定路线。可用前段通过 `Poses` 继续运动，后续块通过 `WaitingForTiles` 与 `RequiredTiles` 公开。新块到达后沿用同一请求版本；目标块取得时，从实际位姿重新连接目标的局部路线。

`HeadingNavigationStreaming` 接收 Scene、`NavigationTileSource<HeadingNavigationMesh>` 与 Scheduler。应用通过 `NavigationInterest` 提供当前位置、路线与前瞻距离，并把 `HeadingNavigationStreamingSystem` 放入自己的后台组。`Update` 读取需求块，`Applied` 只覆盖当次已完成块的接入 Job；主运动调度依赖此句柄。`ResidentTileBudget` 控制驻留数量，路径和已提交 Job 持有的实际数组继续有效。

## 规格变体与保存

`HeadingNavigationMeshBlock.SetMesh` 以 `NavigationMeshSelection` 安装完成结果；选择键继续由 Profile 资产身份和构建变体名组成。Block 保存几何、详细高度、区域区间、平移动作、旋转动作和完整图节点/边。编码与解码使用现有数组 Job。

`GetBlockAsync` 取得变体目录及朝向区域摘要，`CreateAsync` 显式取得完整详细资源，`Create` 共享已经取得的资源。带摘要的变体把各 Tile 保存为独立的十二组数组。`CreateTileSource(selection)` 返回 `HeadingNavigationMeshTileSource`，独立持有文件并公开 `Graph`、`Directory`；`ScheduleRead` 共用读取 fence、工作线程解码与来源释放机制。资源持有自身数据引用，资产关闭后位姿和动作查询继续使用取得的数组。相关身份与目录约定见[导航规格、策略与网格资产](Assets/Asset-Navigation.md)。

以下是分块入口的调用例子，选择键和首块坐标由游戏提供：

```csharp
using HeadingNavigationMeshTileSource source = block.CreateTileSource(selection);
HeadingNavigationMesh first = source.ScheduleRead(firstTile, scheduler).Complete();
var scene = new HeadingNavigationScene(new(source.Graph,
    new HeadingNavigationMeshTile[] { new(firstTile, first, firstBounds) }));
using var streaming = new HeadingNavigationStreaming(scene, source, scheduler);
streaming.Interests.Add(new NavigationInterest(actualPosition, 2) { LookAheadDistance = 8 });
backgroundGroup.Add(new HeadingNavigationStreamingSystem(streaming));
```

实现：[ASA](../../src/SomeEngine.Navigation/AsaNavigationSearch.cs)、[区域摘要](../../src/SomeEngine.Navigation/HeadingNavigationRegionBuilder.cs)、[场景搜索](../../src/SomeEngine.Navigation/HeadingSceneNavigationSearch.cs)、[流送](../../src/SomeEngine.Navigation/HeadingNavigationStreaming.cs)、[资产 Block](../../src/SomeEngine.Assets/Schema/HeadingNavigationMeshBlock.cs)。公开行为入口：[实际位姿路线](../../tests/SomeEngine.Navigation.Tests/AsaNavigationTests.cs)、[资产与分块续行](../../tests/SomeEngine.Assets.Tests/HeadingNavigationMeshTests.cs)。
