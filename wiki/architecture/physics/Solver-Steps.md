# 求解初始化、迭代与互补作用

本页维护当前公共求解阶段、作用量交接及 Body 局部方程职责。源码入口为
[SolveXpbd](../../../src/SomeEngine.Physics/SolveXpbd.cs)、
[SolveAvbd](../../../src/SomeEngine.Physics/SolveAvbd.cs)、
[XpbdForces](../../../src/SomeEngine.Physics/XpbdForces.cs)与
[AvbdBodyEquation](../../../src/SomeEngine.Physics/AvbdBodyEquation.cs)。
运动、反力和具体类型扩展的公开入口见
[XpbdContactTests](../../../tests/SomeEngine.Physics.Tests/XpbdContactTests.cs)。

## 初始化与继续迭代

`SolveXpbd.Create` 和 `SolveAvbd.Create` 的结果都提供 `Initialize()` 与
`Iterate(iterations)`，它们返回具体的 `IPhysicsStage`。原求解对象的 `Schedule`
在一次借用范围内依次执行初始化与配置的迭代。

| 阶段 | XPBD 职责 | AVBD 职责 |
| --- | --- | --- |
| `Initialize()` | 调用所选投影的初始化，开始本子步乘子 | 初始化 Body 方程、所选约束状态及 body→constraint 索引 |
| `Iterate(n)` | 对当前组件执行 n 轮投影，继续更新已有乘子 | 对当前组件执行 n 轮 Body Newton 与 dual 更新，继续使用已有状态和索引 |
| 原 `Schedule` | 一次初始化后执行创建时指定的轮数 | 一次初始化后执行创建时指定的轮数 |

项目在子步中完成一次共同预测、接触检测及维护，再初始化所选求解集合。
同一算法在本子步继续工作时使用 `Iterate`，随后由共同 `UpdateBodyVelocities`
从原子步起点和最终位姿重建一次速度。接触恢复继续消费该速度和共同 `ContactResponse`。
各阶段接收同一个 `PhysicsStepContext`，作业完成依赖连接每次读取与更新。

接触刷新、约束维护、活动预测以及事件发出与消费的完成句柄，同时覆盖原计算和资源清理。
这些物理阶段用原 `ScheduleFinally` 释放命令缓冲或归还借用行，再以
`CombineDependencies([work, cleanup])` 交付共同完成。原计算的异常随同一完成链传给
`PhysicsStepResult.Complete` 和帧的完成消费入口，清理按原生命周期执行。

AVBD 每体的局部预算仍为 `bodyIterations`；每轮继续先装配和分解，再求响应，
调用 `AdjustBodyChange`，最后提交 BodyPose，之后更新约束 dual。

## XPBD 当前作用量进入 AVBD

`IXpbdConstraintForces<TConstraint>` 由同一个具体投影提供参与实体，以及某个 Body
当前的世界力与力矩。内置 Length、Bend、Contact、Joint 和 JointMotor 都通过这一接口
从原组件的当前乘子计算作用量：

```text
Body force  = current linear Jacobian  * λ / h²
Body torque = current angular Jacobian * λ / h²
```

长度和弯曲从当前节点位置重新计算梯度；接触合并同一 Body 在两侧的全部带符号权重和杠杆；
关节与 motor 复用当前框架的共同 Jacobian。motor 同时沿原反馈端选择和逐轴作用上限导出乘子。

`XpbdForces<TConstraint,TProjection>(query)` 以具体 `IAvbdConstraints` 集合消费这些量。
它在初始化时建立可复用参与索引，在 AVBD 装配时把世界力与力矩取负后加入该 Body 的梯度。
XPBD 的 `Initialize` 和 `Project` 继续负责自己的乘子，AVBD 自有集合继续负责其状态与 dual。
共同 `BodyStepState` 的预测值保持积分输入的含义；各约束状态与 Body 方程分别留在原组件上。

以下是将外部接触交给 AVBD、内部长度与弯曲交给 XPBD 的使用例子；查询与物理参数由项目提供：

```csharp
var projected = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengths)
    .And(new XpbdConstraints<XpbdBend, XpbdBendProjection>(bends));
var internalForces = AvbdConstraintGroups.Combine(
    new XpbdForces<XpbdLength, XpbdLengthProjection>(lengths),
    new XpbdForces<XpbdBend, XpbdBendProjection>(bends));
var equations = AvbdConstraintGroups.Combine(
    new AvbdConstraints<ContactPoint, AvbdContactMath>(contacts), internalForces);
var xpbd = SolveXpbd.Create(world, bodies, projected, iterations: 1);
var avbd = SolveAvbd.Create(world, bodies, equations, iterations: 1,
    multiplierRetention: 0, initialErrorFraction: 0);
var initialize = xpbd.Initialize().Then(avbd.Initialize());
var recovery = xpbd.Iterate(1).Then(avbd.Iterate(1));
```

`initialize` 在本子步执行一次。以下是项目具体 `IPhysicsStage.Schedule` 内按显式轮数
安排 `recovery` 的使用例子：

```csharp
for (int round = 0; round < rounds; round++)
    dependency = recovery.Schedule(in context, dependency);
return dependency;
```

用户投影同时实现 `IXpbdConstraint<T>` 与 `IXpbdConstraintForces<T>` 时，沿同一
`XpbdConstraints` 和 `XpbdForces` 入口参与投影与作用量交接。原行提供已声明的其他数据和状态，
具体集合持有查询及派生实体索引；实际 Body 状态由 `PhysicsBodies` 在有效查询范围中访问。

[IPhysicsConstraintBodies](../../../src/SomeEngine.Physics/PhysicsConstraintBodies.cs) 独立描述
实际受作用端、额外只读 Body 和其他引用实体。AVBD 约束与 XPBD 力适配复用该参与信息，
投影可独立提供参与信息。motor 的受作用端沿反馈选择，额外动态参考端继续作为读取依赖。

`PhysicsBodies` 也接受 `EntityQuery` 的 Body 读视图、Body 写视图及引用实体读视图；
`GetRow`、`ReadPose` 等继续访问原组件，`TryReadReference<T>` 从已声明的实际引用实体取得原行。
接触参与边的端点读取沿这一作用域进行。原 `QueryCursor` 构造继续服务完整查询范围。
受限实体视图和实际存储范围的规则见 [Entity Query Jobs](../ECS/Entity-Query-Jobs.md)。
`SolveXpbdGraph` 与 `SolveAvbdGraph` 沿同一初始化、迭代和作用量职责，
将原组件行交给独立岛、共批与颜色范围；具体顺序和资源范围见[实体约束图求解](Graph-Solving.md)。

## 动态节点与转动参与

`BodyMass.InverseInertia` 的上侧三阶张量全零时，动态 Body 使用三维平移局部方程。
`AvbdBodyEquation` 仅对这三个自由度装配、分解和求响应，转动力矩的位姿响应为零。
`InverseInertia` 的零体轴行选择锁定角轴，其余体轴保留原张量耦合。
`AvbdBodyEquation` 在三个平移轴及实际活动角轴上装配和分解；
部分角轴随当前 `BodyPose` 转到世界空间，Jacobian 投影后进入同一局部块，
`SolveResponse` 将响应重建为世界平移和旋转向量。
例如体空间 `diag(0,0,invIzz)` 只产生绕体 Z 的角响应，偏心锚仍与平移共同求解。
完整体惯量继续使用原世界 XYZ 角基与六维耦合块。
[ApplyGyroscopicTorque](../../../src/SomeEngine.Physics/ApplyGyroscopicTorque.cs) 同样依据实际转动参与计算陀螺作用。
动态胶囊、边和面的 reader 继续提供节点实体、局部锚及插值权重，求解从原组件取得对应自由度。
