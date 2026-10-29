# 带时间目标的关节驱动

本页维护已实现的 motor 数据职责、数值模型和公共调用方法。输入与状态见 [JointMotor](../../../src/SomeEngine.Physics/JointMotor.cs)，共同坐标与离散模型见 [JointMotorMath](../../../src/SomeEngine.Physics/JointMotorMath.cs)，算法适配见 [AvbdJointMotorMath](../../../src/SomeEngine.Physics/AvbdJointMotorMath.cs) 和 [XpbdJointMotorProjection](../../../src/SomeEngine.Physics/XpbdJointMotorProjection.cs)。公开运动行为见 [JointMotorTests](../../../tests/SomeEngine.Physics.Tests/JointMotorTests.cs)，实际动画目标接入见 [PhysicsMotorTargetBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/PhysicsMotorTargetBehaviorTests.cs)。

## 目标、参考空间与反馈

motor 与[双框架关节](Joints.md)共用一个关系实体。`JointConstraint` 保存局部框架，受保护的 `DirectedRelationEndpoints<JointConstraint>` 保存 Source 参考端与 Target 受作用端。`JointMotor` 保存驱动参数，`JointMotorTarget` 保存目标框架、线速度、角速度及对应 `Time`。位姿与速度由对应模拟实体的组件持有。

`JointMotorSpace.Source` 使用当前 `SourceFrame` 中的相对关节坐标；目标位置及线速度沿该框架 XYZ 表达，目标朝向相对该框架表达。`JointMotorSpace.World` 使用 Target 局部框架变换后的世界位置和朝向，目标使用世界坐标。线性坐标单位米，角向坐标沿共同关节的旋转向量分量，单位弧度；角速度输入以所选参考坐标中的空间角速度表达，再通过共同 Jacobian 转换成旋转向量的变化率。

`JointMotorResponse.Target` 将作用量写入 Target。`Both` 同时把相反作用量反馈 Source；父空间双方参与相对坐标的响应，世界空间保持世界目标，Source 接收反作用。世界空间的线性反作用经 Target 框架原点相对 Source 质心的力臂计算力矩，角向反作用由对应角向行传递。

每个子步使用完整 `PhysicsStepContext` 的结束时刻采样目标。从输入 `Time` 起，以线速度延伸目标位置，以角速度推进目标四元数；原目标组件保留这份时间基准。调用者可以在后续子步前更新同一组件，或通过用户数值适配在 `Initialize` 中采样新的动画目标。

## 材料、上限与数值状态

| 参数 | 线性坐标 | 角向坐标 |
| --- | --- | --- |
| 目标位姿 | m | rad，来自目标四元数的旋转向量 |
| 目标速度 | m/s | rad/s，输入空间角速度后转换坐标变化率 |
| 弹性 K | N/m | N·m/rad |
| 阻尼 D | N·s/m | N·m·s/rad |
| 逐轴上限 | N | N·m |

采用弹性与阻尼的比例微分驱动，作用随位置误差和速度误差共同变化；共同能力参考 [PhysX D6 驱动](https://nvidia-omniverse.github.io/PhysX/physx/5.4.0/docs/Joints.html#drives)。对时长 h 的子步，c 是当前坐标，c₀ 来自 `BodyStepState` 的步起点，p 与 v 是本子步采样目标；恢复作用的残差为：

```text
r = K * (c - p) + (D/h) * (c - c₀ - h*v)
Keff = K + D/h
```

AVBD 将 `r/Keff` 与 `Keff` 接入同一 compliant ALM 装配。`AvbdMotorState` 持有逐轴约束力、力矩及独立罚系数，作用量按材料上限裁剪；饱和段作用保持常量，其曲率为零。共同 `initialErrorFraction` 应用于本子步起点残差，`multiplierRetention` 控制跨子步保留。局部方程继续由实体的 `AvbdBodyEquation` 持有。

XPBD 使用 `alpha = 1/(Keff*h²)`，`XpbdMotorState` 保存当前子步的位移乘子，并把其幅度限制为 `MaximumForce*h²` 或 `MaximumTorque*h²`。线性乘子的单位为 N·s²，角向乘子的单位为 N·m·s²；除以 h 得到冲量，除以 h² 得到作用力或力矩。两种算法都经共同质量与完整体惯量修正动态实体，再由 `UpdateBodyVelocities` 重建速度。

## 使用例子与动画输入

以下是父空间速度驱动的使用例子。`joint` 是已创建的 `JointConstraint` 有向关系，参与模拟实体与查询由调用方持有：

```csharp
world.Add(joint.Entity, new JointMotor
{
    Space = JointMotorSpace.Source,
    Response = JointMotorResponse.Both,
    LinearDamping = new Vector3(10, 0, 0),
    MaximumForce = new Vector3(20),
});
world.Add(joint.Entity, new JointMotorTarget
{
    Frame = JointFrame.Identity,
    LinearVelocity = new Vector3(10, 0, 0),
    Time = 0,
});
world.Add(joint.Entity, new AvbdMotorState { LinearPenalty = new Vector3(2000) });
var motorQuery = world.Query(new QueryDefinitionBuilder()
    .Read<JointConstraint>().Read<DirectedRelationEndpoints<JointConstraint>>()
    .Read<JointMotor>().Read<JointMotorTarget>().ReadWrite<AvbdMotorState>());
var motors = new AvbdConstraints<JointConstraint, AvbdJointMotorMath>(motorQuery);
```

`motors` 可以与普通关节的 `AvbdConstraints<JointConstraint,AvbdJointMath>` 经 `AvbdConstraintGroups.Combine` 组合。XPBD 使用 `XpbdConstraints<JointConstraint,XpbdJointMotorProjection>` 和自身的 `XpbdMotorState`；两者沿同一 `PredictBodies → 约束求解 → UpdateBodyVelocities` 及完成依赖执行。

XPBD motor 的当前受限乘子也可经 `XpbdForces<JointConstraint,XpbdJointMotorProjection>`
进入 AVBD 世界力与力矩组装，具体反馈端继续沿原 `JointMotor.Response` 选择。
交错迭代、初始化职责及共同预测的调用方法见[求解阶段](Solver-Steps.md)。

参与实体查询包含双方端点，声明 `BodyPose` 的写入、`BodyMass` 与 `BodyStepState` 的读取；AVBD 同时声明 `AvbdBodyEquation` 的写入。查询按所属 World 的生命周期持有，关联物理步完成后释放。

用户 motor 通过既有 `IAvbdConstraint<T>`、`IXpbdConstraint<T>` 或对应集合接口实现静态数值适配。原 `QueryRow` 可以读取同实体的动画输入，并写入独立的数值状态与 `JointMotorTarget`。动画侧的 [PosePredictionBatch](../../../src/SomeEngine.Animation.Pose/PosePrediction.cs)按显式时刻采样既有选定路径，输出模型骨骼及 `PosePredictionSample.Time`；骨骼到刚体的局部映射沿 [PhysicsPoseMap](../../../src/SomeEngine.Animation.Pose/PhysicsPose.cs) 表达。对应适配按 `context.EndTime` 取得目标、变换到所选空间并保留采样时间，再经本页同一 motor 数值入口驱动实体。
