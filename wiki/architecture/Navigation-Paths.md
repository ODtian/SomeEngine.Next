# 目标、路径与实际运动

`NavigationSystems<TSpace,TSearch,TState,TMotion,TReader>` 将目标求解、路径引导、运动建议与实际运动反馈连接起来。`TSpace` 指定导航表示，`TSearch` 使用静态接口推进 `TState`，`TMotion` 与 `TReader` 接入游戏已有的运动数据。多边形网格通过 `NavigationMesh` 接入，搜索类型决定求解方法。

带放置变换的多区域组合使用 `NavigationScene`、`SceneNavigationSearch` 和 `SceneSearchState`。入口图、详细路径分段与区域依赖见[区域入口图与场景放置](Navigation-Regions.md)。

## 角色输入与结果

`NavigationComponents.AddPath<TSpace,TState>` 添加公共输入、输出与每个实体独占的搜索工作区。组合对象的 `AddAgent` 调用同一入口。`NavigationAgent` 引用共享的 `NavigationProfile`、`NavigationPolicy`，并提供期望速度；`PathNavigation<TSpace>` 引用导航表示。

`NavigationGoal` 支持世界坐标点目标与实体目标，实体目标的位置来自同一强类型运动读取器，并加上目标组件中的世界坐标偏移。`ArrivalRange` 给出目标区域半径；`Heading` 和 `HeadingTolerance` 给出到达朝向。`World.Replace` 修改目标或资源引用，`World.Disable<NavigationGoal>` 暂停导航，`World.Enable<NavigationGoal>` 从实际位置重新求解。

目标、目标位置、规格、策略、表示或启用状态变化会推进请求版本。`NavigationPathResult` 保存路径、搜索状态、目标位置与请求版本。`NavigationGuidance` 保存当前期望速度和朝向，`NavigationSteering` 提供运动消费者使用的建议。每个输出携带对应的请求版本。

`INavigationMotionReader<TMotion>.Read` 返回实际位置、速度、朝向和接地结果。朝向遵循 `NavigationHeading`：XZ 平面上的弧度，零朝 +Z，正向由 +Z 转向 +X。`Direction`、`FromDirection`、`Difference` 统一方向转换和角差。

## 完成依赖

`Schedule(deltaTime, expansionBudget, motionReady)` 接收实际运动输入的就绪句柄，返回 `Paths`、`Guidance`、`Steering` 三个完成句柄。每个请求按本次预算继续自己的搜索队列，路径完成后引导角色逐段到达路径拐点，并在预测的下一位置采样表面高度。

运动消费者依赖 `Steering`，写回实际位姿与速度；`ScheduleProgress(motionCompletion)` 等待运动完成后读取结果。进度距离使用 `max(0, distance(actualPosition, targetPosition) - arrivalRange)`；进入目标区域后按实际朝向报告 `Aligning` 或 `Arrived`。其它进度状态包括 `Idle`、`Searching`、`Moving`、`Unreachable` 和 `TargetUnavailable`。

`ScheduleGuidance` 返回路径与引导的完成句柄，供调用方将其与[共享流场](Navigation-Flows.md)和邻居工作汇合，再由所选避让阶段写入本步 Steering。

以下是调用方式的例子，运动组件、资源与目标由游戏提供：

```csharp
var navigation = new NavigationSystems<NavigationMesh, PolygonNavigationSearch,
    PolygonSearchState, Movement, MovementReader>(world);
navigation.AddAgent(entity, mesh, profile, policy,
    new NavigationGoal(position: destination, arrivalRange: 0.25f), desiredSpeed: 2f);

NavigationStep step = navigation.Schedule(deltaTime, expansionBudget: 64, motionReady);
JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, step.Steering);
JobHandle progress = navigation.ScheduleProgress(moved);
progress.Complete();
NavigationProgress result = world.Read<NavigationProgress>(entity);
```

`WorldJobs.CombineDependencies` 在 World 选定的 Scheduler 上合并阶段句柄。组合对象保存上一步完成句柄，使连续调度保持实际运动到导航进度的先后关系。

需要避让的组合使用 `ScheduleGuidance`，与 Flow 群体共同加入[邻居与 ORCA 阶段](Navigation-Avoidance.md)，再消费该阶段写出的 Steering。

具有转弯限制的车辆使用[运动基元与状态格组合](Navigation-Vehicles.md)，路径结果保存位姿和控制序列，实际运动消费同一车辆模型。

## 多边形路径

`NavigationMesh` 保存凸多边形的连续顶点索引、定向入口范围及可选细节三角形。入口的左右端点沿行进方向定义：在 XZ 中，行进方向与“入口中点到左端点”的叉积为正，反向入口交换端点。

`PolygonNavigationSearch` 在定向入口图上执行 A*。每个请求保存开放队列、累计成本和前驱入口；区域成本乘以行进距离，入口成本与动作成本累加，规格及策略的能力集合决定可通行范围。搜索完成后 `NavigationPath.Corridor` 保存多边形走廊，`Corners` 保存 Funnel 拐点，`Cost` 保存选中入口图路线的成本，`Dependencies` 保存沿途块及内容版本。

Funnel 按 [Mononen 的入口收紧算法](https://digestingduck.blogspot.com/2010/03/simple-stupid-funnel-algorithm.html) 生成拐点。`FindPolygon` 按 XZ 包含关系和最近高度层选择多边形，`TrySampleHeight` 使用细节三角形或多边形三角扇插值，拐点和运动引导共享该高度查询。

### Polyanya 几何最短路径

平面凸多边形网格采用统一欧氏距离目标时，组合使用 `PolyanyaNavigationSearch` 和 `PolyanyaSearchState`。规格、区域和入口的能力条件以及策略中的正无穷成本确定可通行范围，`NavigationPath.Cost` 表示以米计的几何路径长度。调用方为这种目标提供统一距离权重及零附加入口、动作成本。

`PolyanyaSearchState` 持有本请求的根点、入口可见区间、根点累计距离、前驱链和优先队列。搜索把区间投影到相邻凸多边形；可见部分保留根点，阴影部分在相应边界端点转向。部分边入口的端点参与边界转角识别。队列使用经过区间的几何距离下界排序，目标节点出队后沿根点前驱链生成位置路径，走廊及块内容版本使用公共路径结果。

以下是选择 Polyanya 的调用例子，后续 `AddAgent`、`Schedule` 与 `ScheduleProgress` 使用上面的公共入口：

```csharp
var navigation = new NavigationSystems<NavigationMesh, PolyanyaNavigationSearch,
    PolyanyaSearchState, Movement, MovementReader>(world);
```

算法依据为 [Cui、Harabor、Grastien，IJCAI 2017](https://www.ijcai.org/proceedings/2017/0070.pdf)，实现入口为 [PolyanyaNavigationSearch](../../src/SomeEngine.Navigation/PolyanyaNavigationSearch.cs)。

## 数据访问

路径中的动作段通过 `NavigationPath.Traversals` 进入共同交接阶段，角色动作消费者提供执行结果与实际出口位置。资源、版本和调度入口见[导航动作连接](Navigation-Actions.md)。

组件列访问与组件引用指向的数据访问分别声明。规格轮廓、策略成本、网格和完成路径数组作为实际只读资源共享；每个请求的可变搜索对象作为独占资源使用。生成式 Job 在语义依赖完成后收集实际引用，按完整访问集合调度角色范围。组件、Job 字段和请求引用保持计算数据有效。

资产创建入口及共享数组寿命见[导航规格、策略与网格资产](Assets/Asset-Navigation.md)，几何和表面构建见[导航几何与多层表面](Navigation-Surfaces.md)。公共行为检查位于 [NavigationMovementTests](../../tests/SomeEngine.Navigation.Tests/NavigationMovementTests.cs)、[PolygonPathTests](../../tests/SomeEngine.Navigation.Tests/PolygonPathTests.cs) 和 [PolyanyaPathTests](../../tests/SomeEngine.Navigation.Tests/PolyanyaPathTests.cs)。
