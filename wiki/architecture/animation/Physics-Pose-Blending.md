# Physics Pose Blending

本页记录已实现的公开接口与姿势换算。源码入口为 [PhysicsPose](../../../src/SomeEngine.Animation.Pose/PhysicsPose.cs)、[实体映射读取](../../../src/SomeEngine.Animation.Pose/ReadPhysicsPose.cs)、[PhysicsPoseSystem](../../../src/SomeEngine.Animation.Pose/PhysicsPoseSystem.cs)、[BoneTransformOps](../../../src/SomeEngine.Animation.Pose/BoneTransformOps.cs) 和 [PoseCompositionSystem](../../../src/SomeEngine.Animation.Pose/PoseCompositionSystem.cs)。阶段职责沿用 [ADR 0019](../../../docs/adr/0019-animation-driven-character-physics.md)。

## 数据与阶段

`PhysicsPoseMap` 共享骨架、刚体顺序和刚体相对骨骼的固定局部变换。`PhysicsBone.Bone` 使用该骨架的自身骨骼编号；`BodyInBone` 将刚体局部坐标转换到对应骨骼局部坐标。每个映射条目对应一个模拟实体，引用沿 `Map.Bodies` 顺序排列。映射在建立时保存逆偏移与骨骼到刚体的直接索引。

`PhysicsPoseInstance.BindBodies(query, entities)` 在安装时绑定模拟实体引用，返回具体 `ReadPhysicsPose` 消费阶段。`query` 声明这些实体的 `BodyPose` 读取，`Bodies` 公开只读实体顺序。实体的真实姿势由共同物理步更新；消费阶段在有效查询中直接将 `BodyPose` 转为原骨骼模型输出数组。`ActorTransform` 是消费结果对应的角色模型到世界输入，每次提交消费时取得该值。

`ReadPhysicsPose` 作为 [PhysicsFrame](../physics/Physics-Frames.md) 的原结果 consumer。完整物理步后读取实体，返回的完成依赖覆盖映射；下一步写入继续依赖消费完成。弱交互帧从已有骨骼模型输出混合当前动画，强交互在原 `CompleteForRead` 入口完成同一消费链。`Step` 公开本次映射所属的 `PhysicsStepResult`：步号、物理起止时刻与映射完成句柄。第一份物理结果到达前，节点直接发布当前 source。

Map、Mask、source、实体引用和各派生数组的访问描述均使用 `PoseAnimation.GetScheduler(world)`，带 `WorldJobs` 的世界使用其选定 Scheduler，普通同步世界沿 `JobSystem.Current`。节点的混合作业依赖映射完成，结果发布与后续 FK 沿原动画阶段执行。

`PhysicsPoseParameters.Source` 引用本次动画目标结果，`Output` 引用最终混合的局部结果。节点读取动画目标和已完成刚体状态，写入自己的 `LocalPose`、`PoseResult`。后续 Blend、Layer 等节点继续消费普通结果，`PoseCompositionSystem` 将所选输出交给骨架完成 FK。最终模型姿势沿既有 [Game world 到 Rendering world](../rendering/GameWorld-and-RenderWorld.md) 提取路径进入蒙皮。

## 坐标换算与局部混合

矩阵采用 [BoneTransform.ToMatrix](../../../src/SomeEngine.Animation.Pose/BoneTransform.cs) 和 [SkeletonInstance](../../../src/SomeEngine.Animation.Pose/SkeletonInstance.cs) 的行向量约定。令 `B = BodyInBone`、`M = boneModel`、`A = ActorTransform`、`W` 为从实体 `BodyPose` 直接计算的刚体局部到世界矩阵，坐标关系为：

```text
bodyPoint * B * M * A = worldPoint
W = B * M * A
M = inverse(B) * W * inverse(A)
```

消费阶段为映射骨骼计算物理模型矩阵，节点从当帧来源的局部姿势计算动画模型矩阵。对已映射骨骼，将其物理模型矩阵乘父矩阵的逆，得到目标局部变换。已映射父骨骼提供物理模型矩阵；其余自身父骨骼提供本次动画 FK 矩阵；连接继承骨骼的根读取父区段已经完成的模型矩阵。自身骨骼的父先子后顺序与普通骨架 FK 相同，映射条目的顺序独立于骨骼层级顺序。

目标局部矩阵分解为 TRS，再使用 `BoneTransformOps.Lerp` 与来源局部姿势混合。每根已映射骨骼的物理贡献为 `BoneMask.Weights[bone] * AnimationLayers.Items[placement.Layer].Weight`。遮罩提供部位权重，通用层权重提供整组物理贡献；权重 1 为遮罩指定的物理影响，权重 0 为当前动画。其余骨骼沿用来源局部姿势。最终 FK 沿混合后的父子链计算，因此部位权重作用于局部姿势。

将该层权重连续动画到 0 即完成恢复。来源动画在恢复期间持续求值；恢复结束后节点继续发布当前来源及其采样时刻、相位、启动标记和根运动。`PoseResult.Time` 保留当前动画来源时钟，物理来源时刻由 `PhysicsPoseInstance.Step` 单独提供。输出的贡献权重由共同图和有效来源决定，物理贡献归零后仍可进入后续合成。

## 使用

以下是接入现成骨架、动画片段和实体物理的使用例子。`bodyInBone`、`bodyEntities` 与 `actorWorld` 由角色数据提供；`simulation` 是已配置的共同物理步，`frames` 是现有 `GameFrameSystems`：

```csharp
var mapping = new PhysicsPoseMap(skeleton, [new PhysicsBone(bone, bodyInBone)]);
var mask = new BoneMask(partWeights);
Entity target = PoseAnimation.CreateTarget(world, skeletonInstance,
    resultCount: 2, output: new PoseResultId(1));
var layers = new AnimationLayers { Count = 2 };
layers.Items[0] = new() { Mode = LayerMode.Override, Weight = 1 };
layers.Items[1] = new() { Mode = LayerMode.Override, Weight = 1 };
world.Add(target, layers);

using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(target,
        new(clip, new PoseResultId(0), Loop: true));
    builder.BlendPhysicsPose(target,
        new(new PoseResultId(0), new PoseResultId(1), mapping, mask), layer: 1);
}
Entity animation = builder.Play();

QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
var binding = (Bodies: bodyEntities, Query: poses, Actor: actorWorld, Read: default(ReadPhysicsPose));
world.ExecuteBufferWrite<PhysicsPoseInstance,
    (Entity[] Bodies, QueryHandle Query, Matrix4x4 Actor, ReadPhysicsPose Read)>(
    animation, ref binding, static (instances, ref binding) =>
    {
        instances[0].ActorTransform = binding.Actor;
        binding.Read = instances[0].BindBodies(binding.Query, binding.Bodies);
    });
var physics = simulation.CreateFrame(binding.Read, stepDuration: 1d / 60,
    maximumDelay: 1d / 30, firstStepNumber: 1, startTime: 0);
frames.Physics = physics;

var weight = Channel<float>.Of<AnimationLayers>(
    static (ref AnimationLayers value) => ref value.Items[1].Weight);
var recovery = Spec.Tween<float, FloatOps, Linear>(1);
PropertyAnimation.To(world, target, weight, 0f, recovery);

AnimationSystems.Add(frames.Animation, recovery);
PoseAnimation.AddSystems(frames.Animation,
    nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
frames.UpdateFrame(deltaTime);
```

原 `GameFrameSystems` 在物理消费、变换与动画之后结束读取窗口，再提交后续到期步。多个角色的具体 `ReadPhysicsPose` 可以通过已有 `.Then(...)` 组成同一 consumer。绑定查询由调用方持有至物理帧完成并释放；实体引用的修改在原消费结束后的绑定阶段进行。

动画提供的模型空间骨骼目标通过 `PhysicsBone.BodyInBone` 与 actor 变换进入共同关节 motor，世界/父局部参考、实际作用端、反馈、子步采样和作用量沿[物理驱动](../physics/Motors.md)。求解后的实体由本页消费阶段沿相反方向映射，最终 `SkeletonInstance.ModelPose` 继续通过 `MeshSkin` 与 `SkinRenderExtractor` 交付原 Rendering world 区段与蒙皮。

通用权重阶段先写入当帧层权重，姿势阶段随后读取。图生命周期和按结果依赖调度见 [Pose Graphs](Pose-Graphs.md)，部位遮罩见 [Pose Layers](Pose-Layers.md)。
