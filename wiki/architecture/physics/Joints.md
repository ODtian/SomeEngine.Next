# 双框架关节

本页维护已实现的关节物理参数、数值状态与公共使用方法。定义见 [JointConstraint](../../../src/SomeEngine.Physics/JointConstraint.cs)，共同坐标与 Jacobian 见 [JointMath](../../../src/SomeEngine.Physics/JointMath.cs)，两种适配分别见 [AvbdJointMath](../../../src/SomeEngine.Physics/AvbdJointMath.cs) 和 [XpbdJointProjection](../../../src/SomeEngine.Physics/XpbdJointProjection.cs)。公开物理步的运动行为见 [JointConstraintTests](../../../tests/SomeEngine.Physics.Tests/JointConstraintTests.cs)。

## 框架与自由度

`JointConstraint` 是有向显式关系的 payload。`DirectedRelationEndpoints<JointConstraint>.Source` 是参考端，`Target` 是受作用端；双方动态实体均通过质量与惯量参与反馈。端点由 ECS 的受保护组件保存，创建、改接与销毁沿现有[显式关系](../ECS/Directed-and-Undirected-Relations.md)维护。

`SourceFrame` 与 `TargetFrame` 各含位置和旋转，位置相对对应 `BodyPose` 的实体原点，旋转相对该实体的局部坐标系。将两个局部框架变换到世界后，线性坐标是两框架原点之差在参考框架中的 XYZ 分量，单位米；角向坐标是参考框架到受作用框架的相对旋转向量的 XYZ 分量，单位弧度。相对四元数选择最短旋转，旋转向量与 Jacobian 沿 Newton 的 jointRotation 数学求值。

每个坐标由 `JointAxisLimit` 单独选择 `Free`、`Locked` 或 `Limited`。`Locked` 的目标为零，`Limited` 的活动区间为 `[Minimum, Maximum]`；坐标超出区间时，单边乘子产生恢复作用。`LinearStiffness` 与 `AngularStiffness` 保存逐轴材料刚度，两种数值适配消费同一份物理参数。

| 工厂 | 线性 X/Y/Z | 角向 X/Y/Z | 物理含义 |
| --- | --- | --- | --- |
| `CreateFixed` | Locked/Locked/Locked | Locked/Locked/Locked | 对齐双方框架 |
| `CreateBall` | Locked/Locked/Locked | Free/Free/Free | 连接双方框架原点 |
| `CreateHinge` | Locked/Locked/Locked | Free/Locked/Locked | 绕框架 X 轴旋转 |
| `CreateSlider` | Free/Locked/Locked | Locked/Locked/Locked | 沿框架 X 轴移动 |

通用关节直接设置这六个轴参数，也可从工厂结果修改各轴；例如把铰链的 `AngularX` 设为 `Limited`，便在同一旋转坐标上应用上下界。

[motor](Motors.md) 在同一关系行上保存带时间目标与弹性、阻尼、逐轴作用上限，并分别配置参考空间与反馈端。

## 数值状态与阶段

`AvbdJointState` 保存逐轴 ALM 乘子、罚系数与步起点误差；`XpbdJointState` 保存当前子步的逐轴位移乘子。两种状态都与物理参数存于同一关系实体。AVBD 的材料刚度与罚系数分别进入 compliant ALM 系数；XPBD 将材料刚度 K 转换为柔度 `1/K`，以 `1/(K*h²)` 进入投影。各端从原位 `BodyPose` 和 `BodyMass` 取得质心、完整体惯量与当前旋转，再写回动态实体的位姿。

[`SolveAvbd`](../../../src/SomeEngine.Physics/SolveAvbd.cs) 显式接收外层 `iterations`，带 `bodyIterations` 的重载允许每体在固定本轮乘子下重复装配、分解与原位更新。原入口每体更新一次。每轮依次更新动态实体，随后统一更新全部约束乘子；每个动态实体的六维局部块次数为 `iterations * bodyIterations`。较大角度的限位运动可通过这一显式预算提高局部非线性求解精度。

AVBD 通过 `AvbdConstraints<JointConstraint, AvbdJointMath>` 接入，XPBD 通过 `XpbdConstraints<JointConstraint, XpbdJointProjection>` 接入。用户关节适配同样实现 `IAvbdConstraint<T>` 或 `IXpbdConstraint<T>`，从原 `QueryRow` 读取端点及其他参数，再经 `PhysicsBodies` 原位访问参与实体；集合组合、作业依赖、查询有效期沿对应求解阶段维护。接口说明见 [XPBD](XPBD.md) 与源文件。

## 使用例子

以下是 AVBD 铰链的使用例子。`world`、`referenceBody` 和 `movingBody` 由调用方创建；参与实体含 `BodyPose`、`BodyMass`、`BodyVelocity`、`BodyStepState` 和 `AvbdBodyEquation`：

```csharp
var parameters = JointConstraints.CreateHinge(
    JointFrame.Identity, JointFrame.Identity,
    linearStiffness: 1e6f, angularStiffness: 1e6f);
parameters.AngularX = new JointAxisLimit(JointAxisMotion.Limited, -0.25f, 0.25f);
var joint = world.CreateRelation(referenceBody, movingBody, parameters);
world.Add(joint.Entity, new AvbdJointState
{
    LinearPenalty = new Vector3(1e6f),
    AngularPenalty = new Vector3(1e6f),
});
var bodyQuery = world.Query(new QueryDefinitionBuilder()
    .ReadWrite<BodyPose>().Read<BodyMass>()
    .Read<BodyStepState>().ReadWrite<AvbdBodyEquation>());
var jointQuery = world.Query(new QueryDefinitionBuilder()
    .Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
    .ReadWrite<AvbdJointState>());
var constraints = new AvbdConstraints<JointConstraint, AvbdJointMath>(jointQuery);
var stages = new PredictBodies(Vector3.Zero, rowsPerPacket: 64)
    .Then(SolveAvbd.Create(world, bodyQuery, constraints, iterations: 32,
        bodyIterations: 3, multiplierRetention: 0, initialErrorFraction: 0))
    .Then(new UpdateBodyVelocities(rowsPerPacket: 64));
var physics = PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, substeps: 1);
physics.Schedule(number: 0, startTime: 0, duration: 0.1).Complete();
world.ReleaseQuery(jointQuery);
world.ReleaseQuery(bodyQuery);
```

共同阶段依次预测、求解并重建速度；返回的完成句柄覆盖本步的全部作业。调用方保持查询有效至关联步骤完成，再沿 World 生命周期释放查询。

项目可沿[长程约束](Long-Range-Constraints.md)为选定理想刚性关节构造路径距离界，继续在同一 AVBD Body 方程与主轮 dual 中求解。
