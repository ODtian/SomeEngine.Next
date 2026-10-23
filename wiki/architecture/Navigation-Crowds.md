# 实际人流与通行成本反馈

`NavigationCrowdSystem<TMotion,TReader>` 在运动消费者完成后读取实际位置和速度，生成普通 `NavigationCrowdField`。`NavigationCostSystem` 将该结果与基础 Costs、拥堵策略组合成新的共享 Costs，再进入[增量流场](Navigation-Flow-Updates.md)。角色继续通过公共 Guidance、Steering 和 Progress 消费更新后的场。

## 统计与单元归并

参与角色添加 `NavigationCrowdAgent(Weight)`，默认权重为 1；组件启用位控制统计参与。`Schedule(grid, motionReady)` 通过 `INavigationMotionReader<TMotion>` 收集已落地角色的实际反馈。支撑定位使用 Grid 的 XZ 单元与最近高度层；四个采样位置沿普通相邻支撑连接取得，保持对应表面关系。

统计先按块和固定数量的角色分区。每个分区写自己的贡献数组，每个角色向周围四个单元中心贡献加权密度和速度乘积。`densityExponent` 控制空间衰减，默认 2。归并阶段按单元整理贡献范围，然后按块分派单元区间；每个单元只有一个写入 Job，写入密度之和与 `sum(weight * velocity) / sum(weight)`。发布的 Field 保存本次独立 Density、AverageVelocity 数组与 Version，旧结果可继续采样。

密度加权速度、向前采样、低密度自由速度与高密度实际速度的混合，以及距离、时间、拥堵不适共同形成的成本，依据 [Treuille、Cooper、Popović 的 Continuum Crowds](https://grail.cs.washington.edu/projects/crowd-flows/continuum-crowds.pdf)。本实现使用 Grid 的连接支撑与普通完成数组，下面两种采样方式作为显式策略保存。

## 拥堵策略与共享 Costs

`NavigationPolicy.Crowd` 保存可选的 `NavigationCrowdPolicy`。`FreeSpeed`、`MinimumSpeed` 规定速度范围，`LowDensity`、`HighDensity` 规定混合区间；`DistanceWeight`、`TimeWeight`、`DiscomfortWeight` 分别表达距离、时间和密度不适偏好。

| Sampling | 密度与实际速度采样 | 写入成本 | 对应求解器 |
| --- | --- | --- | --- |
| Directional | 当前有向边的目标单元；平均速度沿该边方向投影 | 有向 EdgeCosts | ReverseDijkstraFlow |
| Scalar | 当前单元四个相邻轴支撑；平均密度与密度加权速率 | CellCosts 与对应 EdgeCosts | FastIterativeFlow |

策略先按密度在自由速度与采样速度之间插值，再限制到配置速度范围。单位距离成本为 `DistanceWeight + (TimeWeight + DiscomfortWeight * density) / speed`，用自由通行成本归一化后乘到基础 Costs。零速度表示阻断。配置使用正的 FreeSpeed、递增的密度阈值和非负权重。

`NavigationCostSystem.Schedule(baseCosts, crowdBuild, policy, ...)` 等待统计完成，使用本次实际 Field 数组构建新 Costs。每帧传入原始基础 Costs，使区域与动作成本保持同一个起点。省略 policy 参数时使用 `baseCosts.Policy.Crowd`，其后使用默认拥堵配置。`ScheduleApply(costBuild, flows, ...)` 将同一份完成 Costs 安装给所列 Flow。每个 Flow 的求解器随后继续自己的保留状态和目标约束，传播实际变化。

## 运动完成后的调用链

以下是调用顺序的例子；基础 Costs、两个 Flow、具体求解器和实际运动消费者由业务持有：

```csharp
var crowd = new NavigationCrowdSystem<Movement, MovementReader>(world, scheduler);
world.Add(actor, new NavigationCrowdAgent(1));

JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, steeringReady);
NavigationBuild<NavigationCrowdBuildState> measured = crowd.Schedule(baseCosts.Grid, moved);
NavigationBuild<NavigationCostBuildState> adjusted =
    NavigationCostSystem.Schedule(baseCosts, measured, scheduler: scheduler);
JobHandle applied = NavigationCostSystem.ScheduleApply(adjusted,
    new NavigationFlow[] { towardGate, towardExit }, scheduler);
JobHandle gateReady = towardGateSolver.Schedule(256, applied).Completion;
JobHandle exitReady = towardExitSolver.Schedule(256, applied).Completion;
```

后续 Guidance 等待对应场更新完成，局部避让继续读取共享邻居和实际运动，Steering 再交给业务移动系统。实际位移与速度回写后，下一次统计取得新的运动结果。`NavigationPolicyBlock` 保存拥堵采样方式及全部数值配置，资源重开后继续用于同一成本链。

[公开行为检查](../../tests/SomeEngine.Navigation.Tests/NavigationCrowdTests.cs) 使用三块组成的双通道作为输入例子：持续慢行的人流占用一条通道，新角色根据更新后的共享场进入另一条通道并实际到达。方向成本检查经过 Neighbors、ORCA、Steering 和实际速度回写；标量成本检查由 FIM 继续同一工作状态。统计入口见 [NavigationCrowdSystem](../../src/SomeEngine.Navigation/NavigationCrowdSystem.cs)，策略和成本入口见 [NavigationCrowdCosts](../../src/SomeEngine.Navigation/NavigationCrowdCosts.cs)。
