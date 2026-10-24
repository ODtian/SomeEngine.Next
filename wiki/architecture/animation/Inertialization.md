# Inertialization

`InertialPoseLeaf` 将动作切换时的可见姿势与速度差叠加到当前来源，并以临界阻尼衰减。来源和输出沿共同 [Pose Graphs](Pose-Graphs.md) 的结果引用连接；每个安装实例持有自身连续骨骼工作区。源码入口为 [InertialPose](../../../src/SomeEngine.Animation.Pose/InertialPose.cs)、[InertialPoseSystem](../../../src/SomeEngine.Animation.Pose/InertialPoseSystem.cs) 与 [InertialPoseMath](../../../src/SomeEngine.Animation.Pose/InertialPoseMath.cs)。

## 输入与结果

`InertialPoseParameters` 指定 `Source`、`Output` 结果编号，以及正值 `DecayRate`、`Tolerance`。衰减率使用每计算秒的倒数；容差用于判定各通道的偏差和偏差变化率收敛。

`InertialPoseInstance.Action` 是调用方提供的动作身份。改变身份时，从当前可见结果建立新偏差；同一动作的来源采样时间继续由来源节点持有。惯性化叶节点自身重新启动时，按共同 `Starts` 生命周期重新取得来源初值。

调用方在本次图更新前填写 `SourceVelocities`，顺序与来源的 own bones 一致。`BoneVelocity` 包含：

| 字段 | 数值 | 坐标与单位 |
| --- | --- | --- |
| `Translation` | 局部平移的变化率 | 父骨骼坐标，每计算秒 |
| `Angular` | 局部旋转的角速度 | 父骨骼坐标，弧度／计算秒 |
| `LogScale` | 各缩放分量自然对数的变化率 | 每计算秒 |

速度与当前来源姿势、实际采样速度对应。动作来源可提供解析速度，也可在同一连续来源路径上采样取得。缩放各分量为正值，旋转为单位四元数。安装实例复用同一份输入速度数组。

`Velocities` 返回当前输出的逐骨速度，供后续姿势或受力消费者读取。最终局部姿势写入普通 `LocalPose`，由 `PoseCompositionSystem` 发布到骨架。输出 `PoseResult` 保留来源的时间、相位、启动标记和根运动；图权重与动画层权重沿共同规则合成。

以下是已有来源节点写入结果 `0` 时的业务调用示例。`action` 和 `velocities` 由动作选择与来源采样阶段提供，`animation` 是共享图播放到该角色后取得的实体：

```csharp
builder.InertializePose(Entity.Null,
    new InertialPoseParameters(new(0), new(1), DecayRate: 8));

PoseAnimation.AddSystems(systems, nodes: static nodes =>
    nodes.Add(new InertialPoseSystem()));

var input = (Action: action, Velocities: velocities);
world.ExecuteBufferWrite<InertialPoseInstance, (ulong Action, BoneVelocity[] Velocities)>(
    animation, ref input, static (instances, ref input) =>
    {
        instances[0].Action = input.Action;
        input.Velocities.AsSpan().CopyTo(instances[0].SourceVelocities);
    });
```

## 衰减与连续切换

令偏差为 `x`、其变化率为 `v`、衰减率为 `w`。每次计算使用 `j = v + w*x`，并以同一个因子 `e = exp(-w*delta)` 更新为 `x' = (x + j*delta)*e`、`v' = (v - w*j*delta)*e`。平移直接叠加偏差，缩放在对数空间叠加后取指数。临界阻尼的偏差式参考 [Spring Roll Call](https://theorangeduck.com/page/spring-roll-call#inertialization)。

旋转偏差采用最短四元数的旋转向量 `r`，输出为 `Exp(r) * sourceRotation`。角速度先把来源速度旋转到叠加后的父坐标，再加上偏差旋转的角速度；该坐标关系参考 [Propagating Velocities](https://theorangeduck.com/page/propagating-velocities-through-animation-systems#inertialization)。旋转向量导数与角速度通过 SO(3) 左 Jacobian 互换；切换时使用其逆映射建立初始导数，更新时用正向映射交付当前角速度，公式见 [A Micro Lie Theory，式 145–146](https://arxiv.org/pdf/1812.01537)。

动作身份变化的当帧，先沿上一可见结果的平移、角速度和对数缩放速度推进到当前计算时刻，再与当前来源建立偏差。随后的帧按计算增量衰减。零增量切换保持同一可见姿势，正常非零帧切换接续原有运动方向和速度；中途再次切换沿当前修正后的姿势和速度建立偏差。

计算增量来自 `PoseNodeContext.Frame.DeltaTime`；共同 Timeline 暂停时增量为零。来源 `PoseResult.Time` 和 `Phase` 继续用于描述动作采样位置。来源时钟切回起点时，惯性化历史由动作身份与自身计算增量推进。

## 执行与数据

共享参数随编译图保存。安装期解析输入输出并登记 `PoseNode` 依赖，分配来源、输出、来源速度、输出速度和偏差五组连续数组。当前依赖层收集输入后，以具体 Job 批量计算骨骼；数组分别声明读取或写入资源，完成句柄收敛后发布结果，下一层消费者取得本帧输出。

各骨骼的偏差与变化率进入容差后，输出采用来源姿势和速度。叶节点继续随所属图求值，供后续动作身份变化接续下一次切换；播放与释放沿共同图的生命周期控制。

公共行为入口为 [InertialPoseBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/InertialPoseBehaviorTests.cs)：通过共同状态机、惯性化和普通 Blend，将两个角色的移动动作交给最终骨架。
