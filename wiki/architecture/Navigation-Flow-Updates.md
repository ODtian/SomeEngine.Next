# 流场增量更新

游戏通过 `NavigationFlow.SetCosts`、`SetProfile`、`SetGoals` 改变共享输入。`NavigationFlowSystem.Schedule(budget, dependency)` 在前置任务完成后取得该输入，调用具体求解器的 `Update`，继续使用同一个 `ReverseDijkstraFlowState` 或 `FastIterativeFlowState`。目标版本随目标编辑推进，输入版本随输入替换推进；`NavigationCosts` 作为完成对象继续由多个 Flow 共享。

## 保留值与传播

更新按支撑位置、高度层、块身份和详细数据状态映射已有单元及有向边，保留仍有效的累计值。删除的支撑、提高的成本和撤去的目标约束激活依赖失效。失效沿旧结果实际采用的依赖向上游传播，然后由当前可走邻居重新计算。降低成本、新通路与新目标激活局部改进。此前预算保留的队列和当前轮次内已计算的边界变化一起进入新输入。

| 求解器 | 失效依据 | 改进传播 | 每块保留数据 |
| --- | --- | --- | --- |
| ReverseDijkstraFlow | 已采用的正向后继 | 当前有向入边及对应边成本 | 成本优先队列、变化单元 |
| FastIterativeFlow | 二轴更新实际采用的一个或两个上风单元 | 当前上风轴和关联入边 | 活动队列、上风轴、两项依赖 |

单块输入作为一个完整工作范围进入相同的求解链。多块输入沿用[固定边界轮次](Navigation-Flow-Tiles.md)：每块读取已完成的边界副本，写自己的连续范围，再由交换阶段激活上游块。完成的 Field 持有独立结果数组，已经提交的读取继续使用该份数据。

初始化与更新共用 `NavigationTileFlow.FindTargets`，将目标约束写为零成本。FIM 的参与轴取决于当前二轴方程；成本升高时，两项真实上风依赖都参与失效。数值方程和适用范围见[共享目标场](Navigation-Flows.md#积分与安装)。

## 当前可用状态

`NavigationFlowBuildState.TileStates` 和 `NavigationFlow.TileStates` 给出当前输入各块的 `Searching` 或 `Ready` 状态，以及 Input、Goal、Grid、Cost、Residence 版本与 `Detailed` 标记。变化可能影响的上游块在本次传播完成前保持 Searching，已验证保持有效的块可继续使用已有完成场。

`Flow.GetStatus(position)` 按实际支撑层查询局部状态。公共 Guidance 在 Searching 位置输出等待状态，Progress 报告 `NavigationStatus.Searching`。完成场安装后，两者继续消费对应方向；进入缺少详细数据的边界时按流送流程报告 `WaitingForTiles`。`Field.Input` 保留本份采样的输入和目标版本，`Field.ContentVersion` 与 `ResidenceVersion` 分别标记内容和驻留。

Grid 的内容版本来自烘焙或来源重建；`NavigationGridStreaming` 接入和移除详细块时推进驻留版本，并保持内容及共享成本版本。目标在尚待取得的块内移动时，解算阶段使用当前目标重建粗入口条件。详细数据到达后继续修复同一状态。

## 来源到共享成本

`NavigationGridBuildSystem(region)` 接收已完成的 `NavigationRegionUpdate` 与对应 Region Apply 句柄。它使用固定 `NavigationSourceInput` 的 Surface、Profile 和 Policy 构建 Grid、入口图及 Costs，保留版本相同的独立块引用。`Completed` 公开匹配当前来源的完成结果，`CreateTileSource()` 为该结果提供独立块来源。

`NavigationGridApplySystem.Schedule(build, flows)` 将同一份 Costs 应用到所给 Flow，保留各自共享目标。Apply 对照当前来源、设置和 Region Apply 的输入；随后求解器在依赖完成后取得已应用的 Costs。已有部分详细输入保持原来的驻留块集合和驻留版本，当前内容替换这些块的实际数据。共同输入的 Flow 继续共享相同的完成 Costs。

以下是调用顺序的例子，来源系统、Flow 和实际运动消费者由业务持有：

```csharp
NavigationBuild<NavigationRegionUpdate> regionBuild =
    NavigationBuildSystem.Schedule(sources.Schedule(transformsReady), scheduler);
JobHandle regionApplied = NavigationApplySystem.Schedule(regionBuild, scheduler);
NavigationBuild<NavigationGridUpdate> gridBuild = gridUpdates.Schedule(regionBuild, regionApplied);
JobHandle costsApplied = NavigationGridApplySystem.Schedule(gridBuild,
    new NavigationFlow[] { towardGate, towardExit }, scheduler);
JobHandle fieldReady = towardGateSolver.Schedule(256, costsApplied).Completion;
NavigationFlowStep step = navigation.Schedule(deltaTime, fieldReady, motionReady);
JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, step.Steering);
JobHandle progress = navigation.ScheduleProgress(moved);
```

实现入口：[更新与约束](../../src/SomeEngine.Navigation/NavigationTileFlow.Update.cs)、[来源与成本接入](../../src/SomeEngine.Navigation/NavigationGridBuildSystem.cs)、[公开状态](../../src/SomeEngine.Navigation/NavigationFlow.cs)。[公开行为检查](../../tests/SomeEngine.Navigation.Tests/NavigationIncrementalFlowTests.cs) 使用双通道、二维成本区域和新增障碍作为输入例子，观察涨价、降价、目标修改、共享成本、预算等待与实际到达。
