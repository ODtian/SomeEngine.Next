# Physics Pose Blending

本页记录已实现的公开接口与姿势换算。源码入口为 [PhysicsPose](../../../src/SomeEngine.Animation.Pose/PhysicsPose.cs)、[PhysicsPoseSystem](../../../src/SomeEngine.Animation.Pose/PhysicsPoseSystem.cs)、[BoneTransformOps](../../../src/SomeEngine.Animation.Pose/BoneTransformOps.cs) 和 [PoseCompositionSystem](../../../src/SomeEngine.Animation.Pose/PoseCompositionSystem.cs)。阶段职责沿用 [ADR 0019](../../../docs/adr/0019-animation-driven-character-physics.md)。

## 数据与阶段

`PhysicsPoseMap` 共享骨架、刚体顺序和刚体相对骨骼的固定局部变换。`PhysicsBone.Bone` 使用该骨架的自身骨骼编号；`BodyInBone` 将刚体局部坐标转换到对应骨骼局部坐标。每个映射条目对应一个刚体，输入数组沿 `Map.Bodies` 顺序排列。映射在建立时保存逆偏移与骨骼到刚体的直接索引。

每个 `PhysicsPoseInstance` 持有独立的连续 `BodyTransforms` 输入和转换工作区。物理阶段先完成刚体求解及输入写入，再执行消费这些输入的姿势节点。`BodyTransforms` 为刚体局部到世界的矩阵；`ActorTransform` 为同一步中角色模型到世界的矩阵。调用方通过公开 World buffer 写入这组已完成的数据，并保持它们有效到节点计算完成。

`PhysicsPoseParameters.Source` 引用本次动画目标结果，`Output` 引用最终混合的局部结果。节点读取动画目标和已完成刚体状态，写入自己的 `LocalPose`、`PoseResult`。后续 Blend、Layer 等节点继续消费普通结果，`PoseCompositionSystem` 将所选输出交给骨架完成 FK。最终模型姿势沿既有 [Game world 到 Rendering world](../rendering/GameWorld-and-RenderWorld.md) 提取路径进入蒙皮。

## 坐标换算与局部混合

矩阵采用 [BoneTransform.ToMatrix](../../../src/SomeEngine.Animation.Pose/BoneTransform.cs) 和 [SkeletonInstance](../../../src/SomeEngine.Animation.Pose/SkeletonInstance.cs) 的行向量约定。令 `B = BodyInBone`、`M = boneModel`、`A = ActorTransform`、`W = BodyTransforms[body]`，坐标关系为：

```text
bodyPoint * B * M * A = worldPoint
W = B * M * A
M = inverse(B) * W * inverse(A)
```

节点先从当帧来源的局部姿势计算动画模型矩阵，并为映射骨骼计算物理模型矩阵。对已映射骨骼，将其物理模型矩阵乘父矩阵的逆，得到目标局部变换。已映射父骨骼提供物理模型矩阵；其余自身父骨骼提供本次动画 FK 矩阵；连接继承骨骼的根读取父区段已经完成的模型矩阵。自身骨骼的父先子后顺序与普通骨架 FK 相同，映射条目的顺序独立于骨骼层级顺序。

目标局部矩阵分解为 TRS，再使用 `BoneTransformOps.Lerp` 与来源局部姿势混合。每根已映射骨骼的物理贡献为 `BoneMask.Weights[bone] * AnimationLayers.Items[placement.Layer].Weight`。遮罩提供部位权重，通用层权重提供整组物理贡献；权重 1 为遮罩指定的物理影响，权重 0 为当前动画。其余骨骼沿用来源局部姿势。最终 FK 沿混合后的父子链计算，因此部位权重作用于局部姿势。

将该层权重连续动画到 0 即完成恢复。来源动画在恢复期间持续求值；恢复结束后节点继续发布当前来源及其采样时刻、相位、启动标记和根运动。输出的贡献权重由共同图和有效来源决定，物理贡献归零后仍可进入后续合成。

## 使用

以下是接入现成骨架、动画片段和物理计算结果的使用例子。`bodyInBone`、`bodyWorld` 与 `actorWorld` 由角色和物理数据提供：

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

var completed = (Bodies: bodyWorld, Actor: actorWorld);
world.ExecuteBufferWrite<PhysicsPoseInstance, (Matrix4x4[] Bodies, Matrix4x4 Actor)>(
    animation, ref completed, static (instances, ref completed) =>
    {
        instances[0].ActorTransform = completed.Actor;
        completed.Bodies.AsSpan().CopyTo(instances[0].BodyTransforms);
    });

var weight = Channel<float>.Of<AnimationLayers>(
    static (ref AnimationLayers value) => ref value.Items[1].Weight);
var recovery = Spec.Tween<float, FloatOps, Linear>(1);
PropertyAnimation.To(world, target, weight, 0f, recovery);

AnimationSystems.Add(systems, recovery);
PoseAnimation.AddSystems(systems,
    nodes: static nodes => nodes.Add(new PhysicsPoseSystem()));
```

通用权重阶段先写入当帧层权重，姿势阶段随后读取。图生命周期和按结果依赖调度见 [Pose Graphs](Pose-Graphs.md)，部位遮罩见 [Pose Layers](Pose-Layers.md)。
