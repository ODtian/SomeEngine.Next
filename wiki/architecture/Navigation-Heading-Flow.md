# 朝向与动作共享流场

本页记录完成的朝向状态图、共享积分、采样及角色交接。源码入口为 [HeadingNavigationFlow](../../src/SomeEngine.Navigation/HeadingNavigationFlow.cs)、[NavigationFlow](../../src/SomeEngine.Navigation/NavigationFlow.cs) 与 [NavigationFlowTraversal](../../src/SomeEngine.Navigation/NavigationFlowTraversal.cs)。

资产来源、保存与重新创建见[导航资产](Assets/Asset-Navigation.md)。

## 图与成本

`HeadingNavigationFlowRegion` 持有区域标识、世界坐标的 `HeadingNavigationMesh` 和端口数组；`HeadingNavigationFlowPort` 指定端口标识与位置。`HeadingNavigationFlowBuilder.Schedule` 读取这些完成数据、`NavigationLinkSet`、规格及策略，生成 `HeadingNavigationFlowGraph`。

图的 `Nodes` 记录区域、多边形、朝向通道与位姿。`Transitions` 与成本图的有向边一一对应，分别表达 Move、Rotate 和 Traversal。原朝向网格的平移及旋转连接保持其方向；端口状态在包含它的凸多边形中连接同一朝向通道。跨区域连接使用 LinkSet 的方向、启用状态、能力及动作成本。特殊动作保留两侧端点、参数、版本及 LinkSet 引用。普通跨区域移动要求目标通道允许源朝向，抵达后再按目标通道调整朝向。

图的 `Costs.Grid` 是供积分使用的有向状态投影。每个区域形成一个连续状态范围，位置相同的不同朝向仍是各自的节点。移动成本包含距离、规格速度及区域策略；旋转成本包含朝向差与规格角速度；动作包含连接成本与动作策略。禁用连接保留拓扑，其成本为正无穷。

## 共享积分与更新

`NavigationFlow(graph, profile, goals)` 持有共享目标与具体图；当前输入通过 `Input.HeadingGraph` 提供其直接引用。`NavigationFlowSystem<ReverseDijkstraFlow,ReverseDijkstraFlowState>` 复用有向前驱扩展与分区边界交换。目标约束选择目标所在凸多边形中允许其朝向的状态，并把末段位置及朝向调整计入终端成本。

`SetGoals` 更新共享目标，`SetGraph` 更新完成的图与成本。保留工作区按区域、多边形、位置和朝向匹配节点，再沿现有成本变化机制修正累计值。`SetGraph` 按连接标识保留 Passage 对象，使动作队列延续。输入版本、各范围 Searching/Ready 状态与完成字段沿用 [增量流场](Navigation-Flow-Updates.md)。

以下是调用例子，区域、端口和目标由游戏提供：

```csharp
HeadingNavigationFlowGraph graph = HeadingNavigationFlowBuilder.Schedule(
    regions, links, profile, policy, scheduler: scheduler).Complete().Graph;
var flow = new NavigationFlow(graph, profile, goals);
var solver = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
JobHandle integrated = solver.Schedule(expansionBudget).Completion;
```

构建及求解均返回 Job 完成句柄。完成输入数组具有显式读取声明，区域求解写各自状态范围，字段安装后保留完整结果数组。

## 采样与实际运动

`NavigationFlowField.Sample(NavigationPose)` 返回 `NavigationFlowSample`，表达 Move、Rotate、Traversal、Arrived 或 Unreachable，并给出目标位姿、状态、边和动作选择。初次采样把实际位姿连接到同一可行凸区域内的状态；普通移动保持选定朝向，旋转在同一位置进行。

`NavigationSystems<TMotion,TReader>` 为 FlowFollower 提供与普通路径一致的 Guidance、Traversal、Steering 和 Progress。跟随状态保留正在消费的有向边，直到实际位置或朝向完成该段。角度变化受规格角速度限制；字段替换后从实际位姿接入新结果。

动作边使用 [导航动作连接](Navigation-Actions.md) 的 `NavigationTraversalRequest`、`NavigationTraversalResult` 和共享 `NavigationPassageSystem`。消费者在 `NavigationFlowStep.Traversal` 或 `Guidance` 完成后取得请求，写回实际运动和动作结果。完成反馈与实际出口位置共同推进后续采样。多个路径或 Flow 组合可向构造器传入同一个 PassageSystem。

公开行为入口为 [HeadingNavigationFlowTests](../../tests/SomeEngine.Navigation.Tests/HeadingNavigationFlowTests.cs)。宽转向区域、单向窄通道、两个角色及容量一的动作连接属于验收例子；区域、通道、方向、容量和目标通过公共数据表达。
