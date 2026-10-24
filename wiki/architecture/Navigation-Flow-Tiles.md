# 跨块流场

`NavigationGridRegionBuilder.Schedule(grid, profile, policy)` 从 Grid 的实际有向通行边建立自己的区域图。返回的 Grid 同时持有独立详细 `Tiles`、连续 `TileRanges` 和 `RegionGraph`。`NavigationGridEntrance` 标记边界上的支撑单元，块内边权由反向 Dijkstra 的实际累计成本取得，`NavigationGridCrossing` 保留原始跨块边的成本、能力和动作。

## 边界轮次与活动块

带 `TileRanges` 的 Grid 进入分块求解。轮次开始时复制已完成累计值及目标索引，所有活动块读取这份固定边界；每块 Job 通过自己的数组范围写累计值、后继和目标关系。交换 Job 等待本轮任务完成，比较跨块连接的值并激活相应上游块，再建立下一轮边界。预算耗尽时保留当前轮次、各块工作队列及尚待处理的块。

| 求解器 | 每块保留的活动数据 | 详细单元更新 | 方向采样 |
| --- | --- | --- | --- |
| ReverseDijkstraFlow | 按成本排序的前沿队列 | 沿实际入边松弛正向成本 | 后继边 |
| FastIterativeFlow | 活动单元队列、上风轴邻居 | 正标量 Eikonal 二轴更新 | 负梯度插值 |

FIM 在块内使用最近的本地值，在块外使用本轮固定边界。边界与块内活动全部收敛后，独立方向 Job 写各块方向范围。该轮次协议是本项目对 [Emerson 的入口和边界条件组织](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf)的实现；实际详细 Grid 内的数值更新沿用[共享目标场的两种求解器](Navigation-Flows.md#积分与安装)。

`NavigationFlowField.Tiles` 给出完成数组中的各块范围及 Grid、Costs、Goal 版本。`BoundaryDependencies` 保存跨块两侧、单元身份、采用的累计边界值、输入版本和轮次；`Round` 标记整份结果的完成轮次。累计值、方向和边界条件通过同一个 Field 一起安装。多个角色通过普通 `FlowFollower` 引用同一个 Flow 及其完成块数据。

## 分批详细数据

`NavigationGridFlowBuilder.Schedule(graph, acquiredTiles, profile, policy, goals)` 为当前取得的详细数据创建 Flow。已加载块提供实际单元；其余块提供所属入口图的条件单元和块内累计边。目标块取得前，目标连接采用几何成本下界。Dijkstra 沿这些入口条件传播；FIM 将入口图的累计条件用于详细块的上风边界。

`Field.RequiredTiles` 和每块 `Detailed` 标记公开详细数据需求。角色消费当前已完成的详细单元，抵达下一块之前的最后单元中心后报告 `WaitingForTiles`。下一块接入后更新 Costs 输入，既有 Flow 对象继续由角色共享。全部详细数据取得后，各块只通过真实 Grid 边及其边界值求解。

`NavigationGridStreaming(flow, source, scheduler)` 复用 [NavigationTileSource](Navigation-Streaming.md) 的文件保持、I/O fence 和实际字节解码 Job。普通 `NavigationInterest` 的位置、半径、前方方向与路线前看距离控制读取；流场的后继关系补充路线前方需求。`ResidentTileBudget` 限制详细输入块的目录引用，`Reads` 公开后台句柄，`Applied` 覆盖已完成块的接入和 Costs 构建。接入根据固定目录版本匹配块，输入变化时重新取得当前输入及实际数组访问。

应用通过独立后台组驱动 `NavigationGridStreamingSystem`。Flow 的解算依赖 `Applied`，运动依赖解算完成句柄；正在读取的块继续由后台组持有。完成 Field 与在途 Job 持有自身实际使用的输入数组。

以下是调用顺序的例子，来源的粗图和起点块由业务先取得：

```csharp
using NavigationGridTileSource source = gridBlock.CreateTileSource(selection);
NavigationGrid first = source.ScheduleRead(startTile, scheduler).Complete();
NavigationFlow flow = NavigationGridFlowBuilder.Schedule(source.Graph,
    new NavigationGridTile[] { new(startTile, first) }, profile, policy,
    goals, scheduler: scheduler).Complete().Flow;
using var streaming = new NavigationGridStreaming(flow, source, scheduler);
var interest = new NavigationInterest(actualPosition, radius: 2) { LookAheadDistance = 12 };
streaming.Interests.Add(interest);
backgroundGroup.Add(new NavigationGridStreamingSystem(streaming));
var integration = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);

interest.Position = actualPosition;
backgroundGroup.Update();
JobHandle fieldReady = integration.Schedule(256, streaming.Applied).Completion;
NavigationFlowStep step = navigation.Schedule(deltaTime, fieldReady);
```

Grid 资产的变体目录和入口图先取得，每块单元与边分别存储；`NavigationGridTileSource` 独立保持文件来源。完整 `CreateAsync` 将独立块和原始跨块边重组为 Grid。预计算 Flow 资产保存完成的块范围与边界依赖，资产重开后的实际移动继续消费这些数据。

实现入口：[Grid 入口图](../../src/SomeEngine.Navigation/NavigationGridRegion.cs)、[边界轮次](../../src/SomeEngine.Navigation/NavigationTileFlow.cs)、[FIM 块内计算](../../src/SomeEngine.Navigation/FastIterativeTileFlow.cs)、[Grid 流送](../../src/SomeEngine.Navigation/NavigationGridStreaming.cs)。公共行为入口：[资产与分批通行选择](../../tests/SomeEngine.Assets.Tests/NavigationGridStreamingTests.cs)、[Eikonal 边界与移动](../../tests/SomeEngine.Navigation.Tests/NavigationTiledFlowTests.cs)、[完成场资产往返](../../tests/SomeEngine.Assets.Tests/NavigationFlowAssetTests.cs)。
`NavigationGrid.ResidenceVersion` 随详细块接入和移除推进，`ContentVersion` 随内容构建推进。输入替换后，两种具体求解器通过[增量更新](Navigation-Flow-Updates.md)保留工作状态和有效值；`Flow.TileStates` 与 `GetStatus(position)` 表达当前输入的局部完成状态。
