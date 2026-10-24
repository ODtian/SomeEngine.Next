# 持续目标的 D* Lite 路线

`DStarLiteNavigationSearch` 在 `NavigationScene` 的区域入口图上维护一个持续目标的搜索状态。调用方通过相同的 `NavigationSystems` 选择该求解器，连接成本和拓扑更新通过 `NavigationScene.ScheduleApply`、[动态构建接入](Navigation-Dynamic.md)或[动作连接更新](Navigation-Actions.md)进入当前场景。

以下是选择求解器的调用例子，场景与运动组件由业务提供：

```csharp
var navigation = new NavigationSystems<NavigationScene, DStarLiteNavigationSearch,
    DStarLiteSearchState, Movement, MovementReader>(world);
navigation.AddAgent(entity, scene, profile, policy, goal, desiredSpeed);
NavigationStep step = navigation.Schedule(deltaTime, expansionBudget, sceneReady);
```

## 请求状态与修复

`DStarLiteSearchState` 持有同一个 `DStarLiteWorkspace`。工作区保留各节点的 `g`、`rhs`、有序队列、前驱/后继、待更新源节点以及 `km`。普通节点的 `rhs` 取所有后继的 `edgeCost + g(successor)` 最小值；目标的 `rhs` 固定为零。优先键使用 `min(g,rhs) + h(currentStart,node) + km` 与 `min(g,rhs)` 的字典序。

实际起点移动时增加 `km`，下一次修复从当前实际位置重建端点连接。入口节点通过区域、Tile、端口和入口几何位置匹配到既有值；新入口取得初始无穷值，消失入口的出边进入移除更新。新旧出边成本或目标不同的源节点进入持久更新队列。`UpdateVertex` 重算其 `rhs`，替换队列项，并将不一致节点交给后续预算推进。

每个预算单位处理一个待更新源节点、一个优先队列节点，或共同流水线中的一个端点/详细搜索步骤。出队旧键比当前键小时重新排队；`g > rhs` 时接受右值并更新前驱；其余不一致节点先将 `g` 置无穷，再更新自身和前驱。起点一致且队列键达到起点键时交付路线。跨块入口的实际成本可以为零；实现用步数作为累计距离的次级顺序，使零成本连接上的路线提取保持严格顺序，`Path.Cost` 继续表示实际通行成本。

算法依据为 [Koenig、Likhachev，AAAI 2002 的 D* Lite](https://idm-lab.org/bib/abstracts/papers/aaai02b.pdf)。目标不可达判断依据起点 `rhs`，与[作者勘误](https://idm-lab.org/bib/abstracts/Koen02e.html)一致。工作区的 `KeyModifier`、`KnownVertexCount`、`ReusedVertexCount` 和累计 `ExpandedVertexCount` 提供请求诊断。

## 详细路径、流送与动作

`SceneSearchRun` 是普通场景 A* 与 D* Lite 共用的具体流水线。它构造放置后的入口和成本，用预算化多边形搜索连接实际起终点，随后把选定入口路线细化为 `Corridor`、`Corners`、`Sections`、块依赖、连接依赖及 `Traversals`。D* Lite 的持久累计值独立于这次端点和详细路径工作。

场景内容版本变化时，当前请求重接实际起点并修复累计值。驻留变化沿用[详细块流送](Navigation-Streaming.md)的可用前段、`WaitingForTiles` 和 `RequiredTiles`；目标块到达后按实际位姿验证目标局部连接。新详细路线通过 `ResetPathProgress` 交付给共同引导链，现有请求版本保持。游戏修改目标、规格、策略或导航表示时，公共请求入口创建相应的新工作区。

动作连接保留当前 `NavigationLinkSet`、连接版本与参数，细化后进入共同动作交接阶段。角色运动消费者写回真实位置、朝向与动作执行结果，`ScheduleProgress` 继续依据实际运动判断到达。

实现：[请求接入](../../src/SomeEngine.Navigation/DStarLiteNavigationSearch.cs)、[D* Lite 工作区](../../src/SomeEngine.Navigation/DStarLiteWorkspace.cs)、[共同场景流水线](../../src/SomeEngine.Navigation/SceneNavigationSearch.cs)。公开行为入口：[移动中修改入口成本](../../tests/SomeEngine.Navigation.Tests/DStarLiteNavigationTests.cs)、[同一请求逐块续行](../../tests/SomeEngine.Assets.Tests/NavigationStreamingTests.cs)。
