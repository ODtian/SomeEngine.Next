# 实体约束图求解

本页维护已实现的约束参与信息、岛与颜色执行顺序、实际资源范围和公共图求解入口。
源码入口为 [图数据与分块](../../../src/SomeEngine.Physics/PhysicsGraph.cs)、
[连通变化维护](../../../src/SomeEngine.Physics/PhysicsGraph.Changes.cs)、
[SolveXpbdGraph](../../../src/SomeEngine.Physics/SolveXpbdGraph.cs) 和
[SolveAvbdGraph](../../../src/SomeEngine.Physics/SolveAvbdGraph.cs)。
公开运动入口见 [PhysicsGraphSolveTests](../../../tests/SomeEngine.Physics.Tests/PhysicsGraphSolveTests.cs)
与 [PhysicsGraphSimdTests](../../../tests/SomeEngine.Physics.Tests/PhysicsGraphSimdTests.cs)。

## 实体、参与角色与状态

每个具体约束用 [IPhysicsConstraintBodies](../../../src/SomeEngine.Physics/PhysicsConstraintBodies.cs)
描述三类实际实体引用：`GetBody` 给出受作用 Body，`GetReadBody` 给出额外只读 Body，
`GetReadEntity` 给出其他组件引用，例如接触参与边、其他关节或接触历史。
`JointMotorResponse.Target` 的 Source 继续进入只读 Body 集合；Source 空间的驱动沿当前参考位姿取值。
具体类型可实现 `TryGetBody(..., PhysicsBodies, ..., out Entity)`，从本阶段已借用的引用范围读取参与边。
图元数据、AVBD 的 Body 索引和活动误差共同使用此入口；原 `GetBody` 实现沿默认回退继续接入。

动态 Body 的读写依赖形成连通分量。共享静态或运动学 Body 提供只读输入；
每个独立动态 Body 也保留为单节点岛。原查询决定参与实体和组件访问，
实际 Body 运动方式进一步决定写入端；XPBD 只把受作用的动态端放入位姿写集合，
AVBD 为选定动态 Body 执行自身局部方程。

`BodyPose`、`BodyMass`、`BodyStepState`、方程、乘子与接触历史继续由原组件持有。
图保存实体引用、读写角色、邻接索引、颜色和派生范围。
约束 Body 端点、读写角色或成员变化会使原连通分量及新端所在分量进入重建；
其余岛与批次继续复用。小岛共批的成员变化会调整相应批次。
`GetReadEntity` 集合变化沿原约束更新岛、颜色、批次及全局查询的组件引用范围，
继续保留原 Body 邻接、执行次序与活动状态。长程支撑每轮生成的接触引用沿这条范围维护路径接续。

用户 XPBD 投影同时实现 `IXpbdConstraint<T>` 与参与信息即可进入图执行。
力导出由独立的 `IXpbdConstraintForces<T>` 提供，它继承共同的 `IPhysicsConstraintForces<T>`；
`XpbdForces<T,TProjection>` 同时支持原 AVBD 集合与图 AVBD 集合，
从当前乘子和 Jacobian 向同一 Body 方程提供世界力与力矩。
具体作用量和初始化职责见 [求解阶段](Solver-Steps.md)。

图求解器的 `Activity(settings)` 直接复用其原图和查询，按原 `BodyActivity` 维护实际 awake 工作范围；共同时间、预测及接触后唤醒的调用方法见 [Body 活动](Body-Activity.md)。

## 岛、共批与颜色

`PhysicsGraphSettings` 显式选择顺序及批次大小：

| 选项 | 工作划分 | 每轮数值顺序 |
| --- | --- | --- |
| `Islands` | 独立岛；符合 `SmallIslandBodyLimit` 的岛按 `BodiesPerBatch` 共批 | 每岛保留原查询组合顺序及约束行或 Body 顺序 |
| `Colors` | 小岛继续共批；动态体数达到 `LargeIslandBodyLimit` 的岛按颜色及 `RowsPerBlock` 分块 | XPBD 按查询、颜色、块投影；AVBD 按 Body 颜色更新，随后全局 dual |

XPBD 颜色排除动态 Body 的写写及读写冲突，同色块中的原约束行可同时投影。
AVBD 颜色沿约束实际 Body 依赖排除相邻局部方程的并行写读冲突；
每个 Body 的多次局部 Newton 仍由同一个工作项依次执行。
共享静态端的读取可以同时推进。

`UseSimd` 允许具体投影处理同色独立行的向量前缀。
[XpbdLengthProjection.Range](../../../src/SomeEngine.Physics/XpbdLengthProjection.Range.cs)
将原行数据聚合到栈上的 lane 临时值，用 `Vector<float>` 完成原长度模型的距离、
逆质量权重、`Compliance / h²` 和乘子增量，写回原 `XpbdLength` 与 `BodyPose`。
其返回值是已处理的实体前缀长度，剩余行继续调用原标量 `Project`。
其他投影可沿 `IXpbdConstraint<T>.ProjectRange` 提供自己的具体范围实现。

## 资源范围与阶段

图求解先沿原查询捕获成员实体，再通过
[EntityQueryJobs.ScheduleGroup](../ECS/Entity-Query-Jobs.md)
绑定当前组件引用与真实资源，进入一次持有实际组件范围、拓扑和引用资源的工作项。
首次写入前会再次扫描完整查询成员并核对参与角色；变化先更新派生集合，
随后重新捕获和绑定，数值初始化从吻合的集合开始。

XPBD 在这一范围内初始化一次，按岛或颜色同步执行并 Join 每个阶段。
AVBD 先初始化选定方程和约束派生索引；LRC 生成的边随后进入图。
每轮同步完成实际 Body 岛或颜色更新，随后处理自由岛的共同平移与转动方向，
再全局更新 dual 和相应 LRC 索引。
下一轮读取当前参与关系。两种求解共用原投影与 Body Newton 行内核，
AVBD 的 `AdjustBodyChange` 继续位于求响应之后、提交位姿之前。
AVBD 在原组的覆盖访问内持有派生 Body 查询；图的实际参与或引用范围变化后刷新，
自定义引用描述器在各轮重新收集当前资源，数值行继续共享本次原写入版本。

具体范围从原 `QueryRow` 取得组件引用，并共用该次 dispatch 的写版本。
同步派生范围同时核对当前工作项覆盖的组件及引用访问。
资源相容分组沿既有组件资源收集器处理真实列和对象范围；
物理颜色和资源相容性共同决定可同时执行的工作。
`QueryReadResources.Add<T,TResources>()` 注册的描述器同样用于元数据读取和数值阶段，
引用在依赖完成后收集，并在执行前复核绑定。
图元数据沿已有只读引用查询借用一次，再读取各具体约束的参与信息；
接触参与边继续使用原关系组件，引用的访问声明来自原约束查询。

### AVBD 自由岛的共同位移

`SolveAvbdGraph` 在同一组件借用范围内，沿当前岛的数据处理一个共同平移方向。
每个 Body 的旋转保持当前值，所有参与 Body 的质心一起移动 `t`。
惯性项沿该方向的梯度为 `Σ mᵢ(xᵢ − xPredᵢ) / h²`，曲率为 `Σ mᵢ / h²`。
当前约束的线性梯度继续由原 `AccumulateBodyEquation` 提供。
对于只依赖参与体相对位置的约束，两端的线性梯度相消，
惯性项的闭式最小位移为 `Σ mᵢ(xPredᵢ − xᵢ) / Σ mᵢ`。
它直接进入本轮位姿求解，后续速度仍由原完成阶段重建。

共同方向还消费当前约束梯度之和，世界目标与其他外部作用量继续参与该方向的受力。
求解器在当前组件范围内试移位姿、重读同一约束梯度，再恢复原位姿，
以方向导数的变化求曲率；曲率采用惯性项下界，最后写入本轮共同位移。
临时 Body 与原位姿列表由同一个求解对象复用。

同一岛的原 `BodyOrder` 同时提供逐级放大的连续分块，从两个 Body 起处理各子集的共同平移。
每块重读当前位姿、预测质心与全部约束梯度；块内的相对力相消，块边界的约束继续提供实际受力和曲率。
当前旋转固定时，全锁等向点关节的残差对子集平移为仿射函数，
试移后的方向导数变化直接给出这一强耦合方向的曲率。
各子集随后接全岛平移与共同转动，再进入原本轮 dual；临时数据仍由同一个对象持有。

共同转动以当前质量加权质心 `c` 为中心，全部参与体的位置和姿态一起旋转。
令 `rᵢ = xᵢ − c`，其梯度为 `Σ(gAngularᵢ + rᵢ × gLinearᵢ)`。
参考框架中的纯相对坐标在该刚性变换下保持不变，沿此方向的约束导数相消。
世界点坐标的残差随共同转动一起旋转；其等向罚势保持不变，
世界线性乘子对应的 ALM 一次项继续进入当前完整方向梯度。
惯性曲率由每体当前世界惯量及质心绕 `c` 的惯量组成：
`Σ[IWorldᵢ + mᵢ(|rᵢ|²I − rᵢrᵢᵀ)] / h²`，
再以 `|rᵢ||gInertiaLinearᵢ|I` 保留位置惯性项的角向正曲率上界。
共同角方向取各 Body 原角自由度的交集；方程在该空间内求解，
试转后重读原完整方向梯度，以实际方向曲率缩放本轮转动。
平移与转动共用同一组临时位姿，当前 `BodyPose` 是每轮的唯一求解结果。

参与角色取自当前岛和各约束的 Body 写角色。
含固定体、运动学体或只读 Body 角色的岛继续采用原逐体下降路径；
例如 `JointMotorResponse.Target` 的动态 source 保持其只读受力角色。
下一轮图更新继续读取当前约束端点与活动范围。

## 公共调用

以下是内置长度和弯曲采用图求解的使用例子；查询和实体由项目持有：

```csharp
var constraints = XpbdGraphConstraints.Combine(
    new XpbdGraphConstraints<XpbdLength, XpbdLengthProjection>(lengths),
    new XpbdGraphConstraints<XpbdBend, XpbdBendProjection>(bends));
using var solve = SolveXpbdGraph.Create(world, bodies, constraints,
    new PhysicsGraphSettings(PhysicsGraphOrder.Colors,
        SmallIslandBodyLimit: 8, BodiesPerBatch: 64,
        LargeIslandBodyLimit: 128, RowsPerBlock: 64, UseSimd: true),
    iterations: 8);
var stages = new PredictBodies(acceleration, 64)
    .Then(solve).Then(new UpdateBodyVelocities(64));
var simulation = PhysicsSimulation.Create(world, bodyFilter, stages, substeps: 2);
simulation.Schedule(number, startTime, duration, dependency).Complete();
```

`AvbdGraphConstraints<T,TMath>` 与 `AvbdGraphConstraints.Combine` 选择具体 AVBD 集合，
`SolveAvbdGraph.Create` 接收同样的图设置，以及迭代数、乘子保留率、初始误差比例与局部迭代数。
图求解结果同样提供 `Initialize()` 和 `Iterate(n)`，可接入同子步的交错恢复。
后续步沿既有完成句柄接续；求解对象在使用期间持有查询引用和派生容量，
`Dispose` 在工作完成后释放这些查询引用与图资源。
