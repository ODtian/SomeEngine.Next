# 分层栅格与共享目标流场

共享 Flow、目标和世界通路修改的持久恢复见[导航持久存档与恢复](Navigation-Saves.md)。

`NavigationGrid`、`NavigationCosts` 和 `NavigationFlow` 是由游戏持有的普通共享对象。角色通过 `FlowFollower` 引用场，采样阶段写入与路径角色共用的 `NavigationGuidance`；运动建议和实际反馈继续使用 `NavigationSteering` 与 `NavigationProgress`。

## 栅格与通行成本

`NavigationGridBuilder.Schedule(surface, profile, connections, ..., scheduler)` 从 `NavigationSurface` 生成多层单元与明确的有向通行边。构建复用 `NavigationSurfaceTraversal` 的坡度、净空、台阶与轮廓判定。普通流场默认使用 `AllHeadings`：轮廓绕原点的外接圆给出全朝向扫掠范围，叠加角色参考点在整格内的平移范围。`HeadingZero` 使用零朝向轮廓并计入整格平移，`Point` 保留支撑位置和身体净空，供后继姿态与扫掠查询使用。

`Grid.Shape` 记录所用轮廓、身高、坡度、台阶与 `FootprintMode`。`Grid.Cells` 的 `Position` 是单元支撑位置，`Layer` 是该列的表面层序号，`CeilingHeight - Position.Y` 是几何净空；`Area`、`BaseCost` 和 `Tile` 保存分类、基础成本及所属块。`EdgeStart/EdgeCount` 指向该单元的正向边范围，边保存目标单元、基础通行成本、能力要求和动作标识。`IncomingRanges/IncomingEdges` 保存对应的入边索引。

`FindCell` 按 XZ 单元和最近高度层定位，`GetColumn` 取得一列的所有层。`FindAxisNeighbor` 沿已建立的普通通行边寻找相应 X/Z 方向的邻层，供标量求解和局部查询使用。`NavigationGridConnection` 提供明确的层间或动作连接；其方向由 `From/To` 和 `Bidirectional` 表达。

Grid 同时实现 `INavigationSpace<NavigationGrid>`，提供静态资源描述和高度查询，供具体图搜索器通过既有路径组合接入。

`NavigationCostSystem.Schedule` 在 Job 中生成 `NavigationCosts`。单元成本等于基础成本乘以策略区域倍率；正向边使用出发单元成本乘以边基础成本，再加动作成本。策略决定可进入的单元及动作，规格与策略的能力集合决定可用的有向边。多个场直接共享同一个 Costs 对象。

## 积分与安装

`NavigationFlow` 持有 Costs、Profile 与共享的 `NavigationFlowGoal` 数组。每项目标表示一个世界坐标区域及可选到达朝向。目标区域内的单元中心作为零成本种子；小于栅格采样的目标由其所在高度层单元承接。

`NavigationFlowSystem<TSolver,TState>` 为一个 Flow 保存工作区。`Schedule(budget, dependency)` 由前置依赖完成后的 Begin Job 取得实际 `Flow.Input`，继续同一输入的求解。输入变化时调用静态 `Update(state,input)`；返回工作区即可继续该状态，默认返回值使系统调用 `Create`。具体求解器通过静态 `Id`、`Create`、`Update` 和 `Schedule` 表达算法。完成句柄覆盖所排的解算与 Apply 子任务，`Complete()` 返回本次实际使用的状态。

`ReverseDijkstraFlow` 将所有目标种子放入最小成本队列。取出单元 `v` 后，沿每条入边 `u → v` 计算 `edgeCost(u,v) + value[v]`；改进时保存该正向边作为 `u` 的后继并加入队列。一次预算限制本次扩展数量，后续调用保留队列和累计值。这是 [Dijkstra 最短路扩展](https://link.springer.com/article/10.1007/BF01386390) 在显式有向图上的反向多源应用。

`FastIterativeFlow` 与 `FastIterativeFlowState` 实现 [Jeong、Whitaker 的 Fast Iterative Method](https://epubs.siam.org/doi/abs/10.1137/060670298)。它用于各向同性正标量地形：`CellCosts` 表示 XZ 平面单位距离的成本，求解 `|grad T| = cost`，正无穷表示阻断。X/Z 邻居来自能力和策略允许的双向普通通行边，每个方向使用最近高度层。普通边给出可走表面连接，单元标量给出此求解器的通行权重；有向动作与逐边权重由 `ReverseDijkstraFlow` 表达。

FIM 工作区持有累计值、固定目标约束、逐块活动队列和实际使用的上风依赖。一次 `Schedule` 的预算按单元更新次数计数；预算结束时，当前队列与边界轮次留待后续调用继续。每块读取本轮固定的外部边界和自己的本地值，Job 写各自单元范围；轮次交换阶段将变化传播到上游块。相邻轴的最小上风值为 `a`、`b`，`d = CellSize * cost`：`|a-b| >= d` 时使用 `min(a,b)+d`，其余使用 `(a+b+sqrt(2*d*d-(a-b)*(a-b)))/2`。目标种子保持零值，活动集在浮点值收敛后结束。

收敛后的独立输出阶段按单元区间并行计算负梯度、后继及目标关系，然后安装完整的 `NavigationFlowField`。`Sampling` 保存方向消费方式；`SampleDirection(position)` 在实际支撑层上查询方向。Gradient 方式对同一目标和有效表面邻域内的方向作双线性插值，普通轴边约束移动方向，完整的两个轴通路共同确定对角穿越。每次 Guidance 的位移限制在 0.45 格内，再结合当前目标区域调整终段移动。高度由实际通过的支撑层给出。

完成的 `NavigationFlowField` 同时保存累计成本、后继边、方向、目标索引、求解器标识，以及 Grid/Costs/Goal 版本对应的块依赖。Apply 只有在当前输入仍匹配时安装整份场，并将场引用与 `FieldVersion` 一并交换。角色采样使用一份已安装的完整结果；生成式组件资源描述器读取 Flow 对象及其实际数组，并以 `FieldVersion` 重建对应访问集合。

成本、积分、方向和块依赖的分工沿用 [Emerson 的 Flow Field Tiles 组织](https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter23_Crowd_Pathfinding_and_Steering_Using_Flow_Field_Tiles.pdf)，具体存储采用普通共享数组。

Grid 自身的区域入口图、确定边界轮次、活动块传播和分批角色移动见[跨块流场](Navigation-Flow-Tiles.md)。成本、规格、目标和来源变化的保留状态、局部可用性与应用链见[流场增量更新](Navigation-Flow-Updates.md)。

实际人群密度和速度可经 `NavigationCrowdSystem` 与 `NavigationPolicy.Crowd` 反馈为共享通行成本，配置与运动完成后的调用链见[人流成本反馈](Navigation-Crowds.md)。

## 角色调用链

`NavigationComponents.AddFlow` 和 `NavigationSystems<TMotion,TReader>.AddFlowAgent` 添加 FlowFollower 与公共运动输出。`World.Enable/Disable<FlowFollower>` 控制采样。共享目标由游戏通过 Flow 修改，角色组件继续引用同一对象。

`Schedule` 返回 `NavigationFlowStep(Integration, Guidance, Steering)`。采样沿明确的后继单元推进，保留正在到达的单元中心，逐段产生速度与朝向建议。进度阶段等待运动完成句柄，使用实际位置到最近目标区域的几何距离，并使用实际朝向判断到达。

以下是一个调用方式的例子，Surface、Profile、Policy、目标和角色由业务提供：

```csharp
NavigationGrid grid = NavigationGridBuilder.Schedule(surface, profile, scheduler: scheduler).Complete().Grid;
NavigationCosts costs = NavigationCostSystem.Schedule(grid, policy, scheduler: scheduler).Complete().Costs;
var flow = new NavigationFlow(costs, profile, [new(destination, arrivalRange)]);
var integration = new NavigationFlowSystem<ReverseDijkstraFlow, ReverseDijkstraFlowState>(flow, scheduler);
var navigation = new NavigationSystems<Movement, MovementReader>(world);
navigation.AddFlowAgent(entity, flow, desiredSpeed: 2);

JobHandle fieldReady = integration.Schedule(budget: 256).Completion;
NavigationFlowStep step = navigation.Schedule(deltaTime, fieldReady, motionReady);
JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, step.Steering);
JobHandle progress = navigation.ScheduleProgress(moved);
```

路径和流场的组合对象都提供 `ScheduleGuidance`。调用方合并这些 Guidance 句柄与邻居完成句柄，再调用所选的避让或运动建议阶段作为本步 Steering 写入方；移动消费使用该阶段的完成句柄。路径的目标语义和强类型运动接口见[目标、路径与实际运动](Navigation-Paths.md)，共享邻居及圆盘模型见[共同邻居与 ORCA 避让](Navigation-Avoidance.md)。

## 资产创建

`NavigationGridBlock` 按 `NavigationGridSelection(Profile, Variant)` 保存多层单元、通行边、形体范围和内容版本。`Build` 使用同一个 GridBuilder 并存入所选变体，`SetGrid` 接收程序化构建的完成对象。`GetBlockAsync` 取得变体目录、形体和已有入口图；`CreateAsync` 显式取得所选完整 Grid，`Create` 返回已经取得的资源。`CreateTileSource` 为带入口图的变体提供独立详细块来源。

`NavigationFlowBlock.SetFlow` 接收 `NavigationFlowSource` 与 Flow。Source 保存导航数据集、规格、策略、Grid 变体和输入资产版本，Block 保存共享目标与输入版本；调用方可选择保存预计算场数组。

`CreateAsync(loader, scheduler)` 通过强类型资产入口取得 Grid、Profile 和 Policy。输入资产版本、Grid 内容版本和求解器标识匹配时复用预计算场；其余创建过程在 Job 中求解当前输入。泛型重载允许指定其它流场求解器。返回的普通 Flow 保持实际输入与结果数组有效，业务可在资产和 Loader 关闭后继续同步采样。

FIM 的具体调用使用 `NavigationFlowSystem<FastIterativeFlow, FastIterativeFlowState>`；资产取得使用 `CreateAsync<FastIterativeFlow, FastIterativeFlowState>`。预计算场一并保存 `Sampling`、块范围、边界依赖、详细块需求和完成轮次，重开后继续按相应方向方式采样。取得完整 Grid 时，保存的部分详细场通过当前完整输入重新求解。

公共行为检查位于 [NavigationFlowTests](../../tests/SomeEngine.Navigation.Tests/NavigationFlowTests.cs)、[FastIterativeFlowTests](../../tests/SomeEngine.Navigation.Tests/FastIterativeFlowTests.cs) 和 [NavigationFlowAssetTests](../../tests/SomeEngine.Assets.Tests/NavigationFlowAssetTests.cs)。FIM 检查使用均匀区域及一片高成本地形作为输入例子，观察几何方向、预算续算以及角色绕行后的实际到达。
