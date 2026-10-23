# Bone Attachments

挂点消费已完成的模型空间骨骼 G，并通过普通实体层级获得角色变换。骨骼由 `SkeletonInstance` 的连续数组持有，游戏为需要挂接的对象创建实体。

## 绑定与更新

`BoneAttachments.Bind(world, attachment, actor, skeleton, bone, offset)` 安装 `BoneAttachment`，补齐实体变换组件，并将挂点的普通 `Parent` 设为角色。`bone` 使用 `ModelPose.GetBone` 的骨骼编号；绑定保存该接口返回的实时 `BonePose`，后续骨架父实例的挂接变化沿来源表传递。

消费阶段计算 `offset.ToMatrix() * bone.Transform`，使用 Core 的 `TransformQvvs.TryCreateFromMatrix` 转为挂点 `LocalTransform`。输入组合须在 QVVS 的表示范围内。随后 `TransformSystem` 按 `TransformQvvs.Combine` 将局部结果与角色世界变换组合。

下面是注册与绑定的调用示例；武器是通用挂点的一种用途。

```csharp
PoseAnimation.AddSystems(game.Systems);
AnimationSystems.Add(game.Systems, new BoneAttachmentSystem(), AnimationPhase.Composition);

Entity weapon = game.World.CreateEntity();
BoneAttachments.Bind(game.World, weapon, actor, skeletonInstance, handBone,
    TransformQvvs.Identity);
game.Update(deltaTime);
WorldTransform result = game.World.Read<WorldTransform>(weapon);
```

将 `BoneAttachmentSystem` 注册在 `PoseAnimation.AddSystems` 之后，使它读取本次 FK 的结果。`GameWorld.Update` 在这些系统完成后运行实体变换传播，因此本帧角色移动与骨骼动画会共同进入挂点世界姿势。

`BoneAttachments.SelectBone(world, attachment, bone)` 更新绑定读取的骨骼，随后一次消费更新立即采用该选择。调用方可以通过公开组件的 `Offset` 字段调整偏移，再以 `World.Replace` 写回。

## 生命周期

挂点实体通过 `World.DestroyEntity` 释放；骨架实例的使用期由调用方持有。释放骨架时，先停止其动画并释放相应 `PoseTarget`，再调用 `SkeletonInstance.Dispose`。

`BoneAttachmentSystem` 识别绑定骨架或选定骨骼来源区段的释放，将存活挂点最后的 `WorldTransform` 保存为根级 `LocalTransform`，移除 `BoneAttachment` 并解除 `Parent`。后续实体变换传播保留该世界姿势，游戏可以继续使用或释放这个实体。

源码：[绑定数据与操作](../../../src/SomeEngine.Animation.Pose/BoneAttachment.cs)、[消费系统](../../../src/SomeEngine.Animation.Pose/BoneAttachmentSystem.cs)、[QVVS 转换](../../../src/SomeEngine.Core/Math/TransformQvvs.cs)、[GameWorld 更新](../../../src/SomeEngine.Core/ECS/GameWorld.cs)。
