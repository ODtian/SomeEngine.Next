# 实体 XPBD 约束

本页维护已实现的 XPBD 数值阶段、数据职责与公共调用方法。源码入口为 [SolveXpbd](../../../src/SomeEngine.Physics/SolveXpbd.cs)、[约束集合与扩展接口](../../../src/SomeEngine.Physics/XpbdConstraints.cs)、[长度投影](../../../src/SomeEngine.Physics/XpbdLength.cs)与[弯曲投影](../../../src/SomeEngine.Physics/XpbdBend.cs)。公开行为检查见 [XpbdConstraintTests](../../../tests/SomeEngine.Physics.Tests/XpbdConstraintTests.cs)。

## 状态与数值阶段

`BodyPose`、`BodyMass` 和 `BodyStepState` 保存模拟实体的当前状态及共同子步输入。`PhysicsBodies` 借用调用者提供的参与实体查询，通过 `ReadPose`、`WritePose`、`ReadMass`、`ReadStepState` 与 `GetRow` 访问原组件。`GetRow` 同时提供该查询已声明的其他组件访问。

`XpbdLength` 作为无向显式关系的 payload，保存静止长度、柔度与当前子步的乘子；两端由同一关系实体上的 `UndirectedRelationEndpoints<XpbdLength>` 保存，经 `World.CreateRelation` 创建，改接和销毁沿既有关系生命周期进行。长度查询声明该受保护端点组件的读取。`XpbdBend` 约束实体保存连续两条边的三个端点、方向夹角的最小余弦、柔度与单边乘子。每次 XPBD 求解开始时重置所选约束的乘子；同一子步的各轮投影累积这份状态。

长度投影使用 `alpha = Compliance / h²`，由长度误差与已有乘子计算本轮乘子增量，按两端的逆质量修正位置。弯曲投影由方向余弦误差计算三端梯度，将乘子投影到大于等于零的范围，再按各端逆质量修正位置。静态与运动学端点提供位置输入，动态端点接受位置修正。两种数学分别来自动画次级链的长度和弯曲投影。

`PredictBodies` 先写入共同步起点与预测结果，`SolveXpbd` 负责约束投影，`UpdateBodyVelocities` 根据同一子步的起点与求解后位姿重建速度。`PhysicsSimulation` 提供子步时长与阶段依赖。

## 查询、组合与扩展

调用者显式创建参与实体查询和各约束查询，沿既有 World 生命周期持有这些查询。参与实体查询声明求解需要的位姿写入、质量与其他组件读取；每个约束查询声明实际物理参数读取和数值状态写入。`SolveXpbd.Create` 在初始化时汇总这些声明，后续步进复用同一个访问数组。

以下是两种内置约束共同求解的使用例子；`world`、`lengthQuery`、`bendQuery` 和模拟实体由调用方创建：

```csharp
var bodyQuery = world.Query(new QueryDefinitionBuilder()
    .ReadWrite<BodyPose>().Read<BodyMass>().Read<BodyStepState>());
var constraints = new XpbdConstraints<XpbdLength, XpbdLengthProjection>(lengthQuery)
    .And(new XpbdConstraints<XpbdBend, XpbdBendProjection>(bendQuery));
var stages = new PredictBodies(acceleration, rowsPerPacket: 64)
    .Then(SolveXpbd.Create(world, bodyQuery, constraints, iterations: 8))
    .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
var physics = PhysicsSimulation.Create(world, bodyFilter, stages, substeps: 2);
PhysicsStepResult result = physics.Schedule(number, startTime, duration);
result.Complete();
```

每轮按集合组合顺序依次投影全部约束，然后推进下一轮。原求解入口通过所属 World 的 Scheduler 提交一个作业，模拟实体组件在外层查询中持续借用，各约束在自己的查询回调内读取参数并更新乘子，完成依赖覆盖全部投影。

`Initialize()` 与 `Iterate(iterations)` 可分别接入同一子步的显式阶段。
初始化、继续迭代、当前乘子作用量交接及实际 Body 自由度的公共用法由
[求解阶段](Solver-Steps.md)维护。共同点接触的 XPBD 物理参数、数值状态与恢复入口见[接触](Contacts.md)。

用户适配器以具体 struct 实现 `IXpbdConstraint<TConstraint>` 的静态 `Initialize` 和 `Project`。两者接收主组件的只读引用、原 `QueryRow` 与共同的 `PhysicsStepContext`；`Project` 还接收 `PhysicsBodies`，从 `context.Duration` 取得子步时长，并可读取该子步的起止时刻。适配器可从原行读取同实体的其他已声明组件，并写入自己的数值状态组件。用户集合沿同一个 `XpbdConstraints<TConstraint,TProjection>` 入口与内置集合组合。

[双框架关节](Joints.md)沿同一集合入口使用共同的关节物理参数，并在独立的 `XpbdJointState` 中保存乘子。

[实体约束图求解](Graph-Solving.md)提供同一具体投影的独立岛、共批和颜色范围执行入口。
