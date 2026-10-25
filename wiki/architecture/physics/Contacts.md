# 持久接触与标准响应

本页维护当前公共接触数据、AVBD 数学及物理步用法。源码入口为 [接触组件](../../../src/SomeEngine.Physics/ContactPoint.cs)、[接触刷新](../../../src/SomeEngine.Physics/ContactPoints.cs)、[检测阶段](../../../src/SomeEngine.Physics/DetectContactPoints.cs)、[AVBD 响应](../../../src/SomeEngine.Physics/AvbdContactMath.cs)与[恢复阶段](../../../src/SomeEngine.Physics/ApplyContactRestitution.cs)。公开运动和生命周期入口见 [AvbdContactTests](../../../tests/SomeEngine.Physics.Tests/AvbdContactTests.cs)。

## 接触身份与作用端

几何检测的 `CollisionContact` 与双方 `ContactBody` 跨度是本次检测的派生输出，原几何及模拟状态继续由实体组件持有。`ContactPointOutput` 将结果写入可复用的 `ContactPointBuffer`，接触头和双方作用端分别使用连续容量。检测的形状、reader 与流形语义见[实体几何](Geometry.md)和[动态几何](Dynamic-Geometry.md)。

持久的 `ContactPoint` 是无向原生关系的 payload；受保护的 `UndirectedRelationEndpoints<ContactPoint>` 持有双方几何实体及其原始位置角色。点 payload 保存特征、流形点序号、双方几何局部接触点、世界见证中点、法向、间距及采样锚基准。每个实际作用端通过 `ContactParticipant` 有向关系连接 Point→Body；受保护的 Target 是该作用端的模拟实体。点上的 `ContactBodyLink` Buffer 保存参与边引用、唯一的 body 局部采样锚及权重。

一个 Body 可在一侧或双方出现多次。数值适配先合并该 Body 的全部带符号权重和相对质心杠杆，形成一个六维 Jacobian；body→constraint 派生索引让该点进入该 Body 的方程一次。这样同一刚体的多个采样点、两节点胶囊及三节点面共用相同响应入口。

`BodyAnchorDelta` 是刷新时双方加权世界采样锚的差，方向为第二方减第一方。求解时的接触间距为：

```text
gap = Separation + dot(Normal, currentWeightedAnchorDelta - BodyAnchorDelta)
```

几何表面偏移由 `Separation` 保存；模拟节点的采样锚及插值权重保持各 reader 的含义。`FirstBodyCount` 划分连续作用端中的双方，Normal 从第一方指向第二方。

## 显式范围与跨步匹配

`ContactPoints.Create(world, poses, points, detected, combine, components, settings)` 接收项目的只读位姿查询、只读接触维护查询、派生检测输出、具体材料合成器、具体新点组件写入器及刷新设置。维护查询的成员筛选由项目显式选择；管理器沿原筛选补充所需读取，在创建时取得自己的查询引用，并在 `Dispose` 中释放。

`IContactPointComponents.Add` 在第二段结构命令中为新点记录算法状态和成员筛选组件。多个接触组可以使用各自的 tag、组件条件和查询进行独立刷新。`AvbdContactComponents` 为新点添加项目给定的 `AvbdContactState` 数值参数，用户写入器通过相同入口添加本组组件。`IContactMaterialCombine` 从双方几何实体的 `ContactMaterial` 组合点材料；`AverageContactMaterials` 对六个材料字段逐项取算术平均。

刷新按几何身份对建立可复用索引，在 `MatchDistance` 的世界距离与 `MinimumNormalDot` 的法向条件内选择最近旧点，并为每个旧点保留一个匹配。匹配点复用实体、参与边和算法历史，原位更新点数据与局部锚；实际作用端变化经原生关系重定向。所选范围内未匹配的点释放，其参与边沿原生关系生命周期回收。Body 销毁后，其参与边结束；接续刷新结束对应旧点，再为后续有效检测建立接触。

`WarmStart` 由刷新设置写入点。AVBD 初始化将旧法向与切向乘子还原为世界力，投影到当前法向和切平面，乘以求解器显式给出的保留系数，再约束到静摩擦圆盘。新点参数、匹配阈值、热启动开关、保留系数及每步刷新时机均由项目提供。

## 材料与数值状态

| 数据 | 含义与单位 | 持有者 |
| --- | --- | --- |
| NormalStiffness | 法向有限材料刚度，N/m | ContactMaterial |
| NormalDamping | 接近时的法向材料阻尼，N·s/m | ContactMaterial |
| StaticFriction / DynamicFriction | 静/动摩擦系数 | ContactMaterial |
| Restitution / RestitutionThreshold | 恢复系数、启用恢复的接近速度阈值 m/s | ContactMaterial |
| NormalPenalty / TangentPenalty | ALM 法向与切向数值度量，N/m | AvbdContactState |
| NormalMultiplier / TangentMultiplier | 法向与世界切向力乘子，N | AvbdContactState |
| FrictionRegularizationSpeed | 滑动方向曲率的速度正则参数，m/s | AvbdContactState |

法向材料使用共同 `AvbdMaterialTerms`，将材料 K 与独立的 ALM ρ 组合为有限刚度系数，并把法向乘子投影到小于等于零的压缩范围。切向试力位于静摩擦圆盘内时使用静止分支，超过静摩擦界限时投影到动摩擦圆盘；界限以切向向量模长与法向压缩力计算。装配使用法向、滑动方向及其切平面正交方向的力与曲率，进入同一 Body 六维块。接近阻尼沿子步起点间距与当前间距计算，加入法向力和法向曲率。

这些 ALM、单边法向、圆盘摩擦和接近阻尼数学从研究分支 `codex/job331-380` 的 `NewtonRigid.cpp` 中 `contactEquation` 与 `addContact` 迁移，来源与 Apache 许可由 [Newton NOTICE](../../../src/SomeEngine.Physics/ThirdParty/Newton/NOTICE) 维护。矩阵分解、当前位姿及速度继续复用共同 AVBD 与物理状态。

## 物理力、恢复与阶段组合

`ContactResponse` 保存本子步的共同物理量：`InitialNormalVelocity` 为双方相对法向接近速度，负值表示接近；`ForceOnSecond` 为作用于第二侧的世界力 N，正压力沿 Normal；`RestitutionImpulse` 为第二侧已施加的正法向冲量 N·s；`StartTime` 与 `EndTime` 标明该作用区间。算法自身的乘子、数值度量和迭代初值分别由算法状态组件持有。

`InitializeContactResponses` 在预测及刷新后读取实际作用端的速度，建立共同恢复输入并清零本子步的作用结果。AVBD 的 dual 更新向 `ForceOnSecond` 写出当前法向、切向及接近阻尼力。共同 `UpdateBodyVelocities` 从本子步位姿重建速度，随后 `ApplyContactRestitution` 根据材料恢复系数、初始接近速度及当前正压力施加法向冲量。冲量按合并 Jacobian、逆质量与完整逆惯量分配给动态端，运动学端提供其实际子步速度。恢复阶段的迭代数由项目显式提供。

以下是阶段组合的使用例子；`world`、查询、候选来源、类型 bins 和具体检测由项目创建：

```csharp
using var detected = new ContactPointBuffer(scheduler, contactCapacity: 256, bodyCapacity: 512);
var narrow = ContactNarrowPhase.Create(bin,
    CollisionGeometry<SphereGeometry, PlaneGeometry>.Detect(
        new ConvexPlaneCollision<SphereGeometry>(1e-5f),
        new ContactPointOutput(detected), contactDistance: 0));
using var detection = DetectContactPoints.Create(world, pairs, bins, classifier, narrow,
    detected, detectionInputs);
using var points = ContactPoints.Create(world, poses, pointScope, detected,
    new AverageContactMaterials(),
    new AvbdContactComponents(new AvbdContactState
    {
        NormalPenalty = 1e6f, TangentPenalty = 100, FrictionRegularizationSpeed = .01f,
    }), new ContactRefreshSettings(.02f, .99f, WarmStart: true));
var constraints = new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts);
var stages = new PredictBodies(acceleration, 64)
    .Then(detection).Then(points.Refresh())
    .Then(new InitializeContactResponses(world, bodies, contacts))
    .Then(SolveAvbd.Create(world, bodies, constraints, 32, 1, 0))
    .Then(new UpdateBodyVelocities(64))
    .Then(new ApplyContactRestitution(world, velocityBodies, contacts, iterations: 1));
PhysicsSimulation.Create(world, bodyFilter, stages, substeps: 1)
    .Schedule(number, startTime, duration).Complete();
```

`bodies` 声明位姿写入、质量/共同步/速度读取及 `AvbdBodyEquation` 写入。`contacts` 声明点、材料、`ContactBodyLink` 读取，参与边受保护端点的可选读取及 `AvbdContactState`、`ContactResponse` 写入。`velocityBodies` 声明位姿与质量读取及速度写入。查询及检测输入资源沿现有 `QueryJobAccess` 进入调度器。

`DetectContactPoints` 在创建时通过 [RelationQueryReads](../ECS/Relation-Queries.md) 登记成员和完整访问查询。
普通组件路径在同一借用范围完成 count、prefix、write 和所选各 bin 的具体检测。
`ContactNarrowPhases.Combine` 组合多个具体窄阶段，每个窄阶段可以提供自身的
`QueryReadResources` 描述；`IContactNarrowPhases.Detect` 同时接收当前
`PhysicsStepContext` 与有效组件视图，具体几何窄阶段沿原距离内核执行。
资源路径先在一组借用范围中填充全部 bins，随后由
`ScheduleEntities` 直接投影本轮 pair 的实体，在输入完成后绑定各实体当前引用的数据；
取得资源并复核绑定后，沿同一预登记读取对象重开有效视图，每个所选 bin 执行一轮检测。
引用资源的等待、复核和完成链见 [Query Reads](../ECS/Query-Reads.md)。
`detectionInputs` 声明候选源与具体内核使用的其他外部数据。

刷新依次连接匹配与点结构回放、参与边及新点组件结构回放、最终锚引用写入；返回依赖覆盖这些工作及命令缓冲释放。后续求解、速度和恢复继续消费该依赖。各模拟实体始终由原组件保存真实状态，派生容量和跨实体索引由对应阶段复用。

用户响应以具体 `IAvbdConstraint<T>` 接入相同集合；`GetBodyCount`、`GetBody` 和各数值阶段均接收完整 `PhysicsStepContext`，可以沿同一受保护参与关系读取作用端，并通过原 `QueryRow` 更新自己的状态。

显式选用[长程约束](Long-Range-Constraints.md)时，`AvbdSupportedContactMath` 将接触自己的 LRC 世界辅助力加入总压力与摩擦投影，原 AVBD 乘子和辅助反力分别保存在各自组件。共同 `ContactResponse` 继续向恢复和后续消费者交付实际总力。
