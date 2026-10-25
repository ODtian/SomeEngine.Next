# 共同邻居与 ORCA 避让

路径角色与共享流场角色使用同一个 `NavigationNeighborSystem<TMotion,TReader>`。`NavigationMotion` 提供实际位置、速度、朝向、着地状态和交互层 `Layer`；读取器把业务运动组件转成这些数据。索引按 XZ 单元和实际层组织角色，查询同时应用邻居距离、身体高度区间和交互掩码，返回距离排序的邻居索引。

`NavigationNeighbors` 持有可复用的角色数组、实体下标目录、空间散列、连接数组及每个角色的邻居范围。拓扑只读根工作先按查询形成实体下标，生成式并行 Job 通过该目录写各角色的独立数组位置，并沿 `NavigationAgentResources` 绑定 Profile 的使用数据。

空间散列按格坐标与实际层计算唯一分区归属。协调阶段将角色下标分发到各分区，每个分区 Job 独立建立格头和连接；角色在各格内沿原查询顺序加入，查询继续沿原格遍历和距离比较规则选择邻居。构建汇合后再启动按角色分区的邻居查询 Job，预测收集随后完成。返回句柄覆盖收集、索引、查询和预测，各阶段继续由原 `NavigationNeighbors` 持有本轮数据。`Agents` 与 `GetNeighbors(entity)` 在完成句柄之后提供本步实际数据。

## 圆盘模型与参数

`AvoidanceAgent` 的 `Radius` 是世界坐标 XZ 圆盘半径，覆盖 `NavigationProfile.Footprint` 绕角色原点旋转时的轮廓。`Spacing` 扩大该角色的圆盘；两个角色的配置距离是各自 `Radius + Spacing` 的和。Profile 提供身体高度和最大速度，实际层及高度区间决定角色之间的空间交互。

`NeighborDistance` 和 `MaximumNeighbors` 定义局部交互范围与数量。`TimeHorizon` 和 `BoundaryTimeHorizon` 以秒计，分别用于角色与静态边界的有限时域速度障碍。正数 `Responsibility` 按双方权重分摊速度修正；相同权重各承担一半。`InteractionMask` 的共同位选择参与同一次交互的角色。

`OrcaAvoidance` 读取期望速度和同一步采集的实际速度，为静态边界及相邻圆盘构造 ORCA 半平面，在 Profile 最大速度圆内选择最接近期望速度的结果。静态边界约束先进入求解，拥挤情况下的投影优化保留这些边界。这个具体运动模型按本步建议直接选择速度；运动消费把实际采用的速度和位置写回业务组件，后续导航继续读取实际反馈。

每个角色的 `NavigationAvoidanceWorkspace` 保存本角色的边界、搜索队列、约束和投影数组。`INavigationAvoidanceSolver` 接收共同的 `NavigationAvoidanceInput` 和工作区，让其它具体求解器复用同一邻居集合与调度顺序。

具有加速度限制的角色选择 [`AccelerationVelocityAvoidance`](Navigation-Acceleration.md)，它使用相同邻居、边界、Steering 和实际反馈链，并在完成的 Steering 中保存本步速度响应模型。

具有完整轮廓及车辆控制约束的角色选择 [MPPI 局部运动轨迹](Navigation-Mppi.md)，使用相同实际运动、邻居、Guidance 和 Costs 输入，向运动与动画交付同一选中控制序列。

## 局部可走边界

`INavigationSpace<TSpace>.CollectBoundaries` 提供表示特定的局部查询。`NavigationBoundarySegment` 沿线段右侧保留可走空间，并记录已经应用到数据中的 `BakedRadius`。

Mesh 从当前多边形沿可通行入口查询附近多边形，在每条边上扣除可通行入口子区间，剩余线段参与避让。Grid 从当前高度层沿可通行有向边查询邻近单元，打开对应的普通相邻面，保留其余边界。能力、区域策略、层高和动作类型共同决定局部可走范围。

Mesh 构建把 Profile 旋转圆半径记入 `NavigationMesh.BakedRadius`，Grid 的全朝向模式使用已记录的 Shape。ORCA 将边界约束半径设为 `max(0, Radius + Spacing - BakedRadius)`，因此角色轮廓已经应用的侵蚀与运行时补充间距共同得到一次完整圆盘间距。

Mesh 的区域图附加和独立块提取保留这个半径。场景变换按 XZ 线性部分的最小奇异值缩放，记录变换后侵蚀椭圆的内含圆半径，使运行时继续使用世界坐标的圆盘间距。

## 调度与运动消费

`NavigationComponents.AddAvoidance` 添加避让参数和每角色独占工作区。以下是同时组合路径角色与 Flow 群体的调用例子；具体角色、空间和运动组件由业务提供：

```csharp
var neighbors = new NavigationNeighborSystem<Movement, MovementReader>(world, scheduler);
var avoidance = new NavigationAvoidanceSystem<OrcaAvoidance, Movement, MovementReader>(world, neighbors);
NavigationComponents.AddAvoidance(world, entity, new AvoidanceAgent(radius, Spacing: spacing));

JobHandle pathGuidance = paths.ScheduleGuidance(deltaTime, expansionBudget, motionReady).Guidance;
JobHandle flowGuidance = flows.ScheduleGuidance(deltaTime, fieldReady, motionReady).Guidance;
JobHandle neighborReady = neighbors.Schedule(motionReady);
JobHandle pathBoundaries = avoidance.SchedulePathBoundaries<NavigationMesh>(motionReady);
JobHandle flowBoundaries = avoidance.ScheduleFlowBoundaries(motionReady);
JobHandle steering = avoidance.Schedule(deltaTime,
    WorldJobs.CombineDependencies(world, [pathGuidance, flowGuidance]), neighborReady,
    WorldJobs.CombineDependencies(world, [pathBoundaries, flowBoundaries]));
JobHandle moved = new MoveCharacters(deltaTime).ScheduleParallel(world, steering);
JobHandle progress = WorldJobs.CombineDependencies(world,
    [paths.ScheduleProgress(moved), flows.ScheduleProgress(moved)]);
```

调用方为本步选择一个 Steering 写入阶段。路径和 Flow 的 `ScheduleGuidance` 产生引导，避让等待引导、邻居和边界，移动消费等待避让，进度等待实际移动完成。索引对象声明实际资源访问，角色工作区通过组件资源描述符独占，空间数组通过表示特定的描述符只读共享。

算法依据是 [van den Berg 等人的 ORCA 论文](https://gamma.cs.unc.edu/ORCA/publications/ORCA.pdf)。约束与求解改编自作者的 [RVO2 C# 实现](https://github.com/snape/RVO2-CS/blob/8e7cf2f9bdc331fe0802cdfb10d6c19fb5b20745/RVOCS/Agent.cs)，归属及 Apache 2.0 许可保存在 [ThirdParty/RVO2](../../src/SomeEngine.Navigation/ThirdParty/RVO2/NOTICE)。

公开行为检查见 [NavigationAvoidanceTests](../../tests/SomeEngine.Navigation.Tests/NavigationAvoidanceTests.cs)：路径角色与 Flow 群体相向经过时保持配置距离并推进目标；原始边界与已侵蚀边界提供相同圆盘间距；共同索引按实际层、身体高度、范围和交互掩码选择邻居。
