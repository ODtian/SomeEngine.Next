# GPU Stride and Body Orientation

本页记录现行步幅、骨盆高度与躯干朝向运算。源码入口为 [GpuStridePose](../../../src/SomeEngine.Animation.Pose/GpuStridePose.cs)、[共同来源链](../../../src/SomeEngine.Animation.Pose/GpuPoseRig.cs)、[正常提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[骨索引与后代范围](../../../src/SomeEngine.Render.Features/Skinning/GpuIkTables.cs) 及 [WarpStride](../../../assets/Shaders/gpu_pose.slang)。

速度比、移动方向补偿和脊柱分配参考 Epic 的 [Pose Warping](https://dev.epicgames.com/documentation/unreal-engine/pose-warping-in-unreal-engine?lang=en-US)。下面的接触混合、共同可达高度和 GPU 数据组织由本项目实现。

## 定义与实例输入

`GpuStridePose` 引用一个 `GpuPoseSource` 和共享 `GpuStrideRig`。Rig 保存骨盆自身骨编号、模型空间上方向、腿部 `GpuTwoBoneChain` 与目标编号、按祖先到后代排列的 `GpuStrideSpine`，以及步幅比例、旋转角和骨盆位移限值。`FacingAxis` 是末端 spine 骨骼的局部朝向轴，spine 为空时采用骨盆；默认值为 `+Z`。每个 spine 权重表示它承担的朝向修正份额；总和为 1 时，链末端取得受角度限值约束的目标 facing。

`GpuStrideLeaf` 沿 `GpuRigPlayback` 安装共同动画实例；`Duration` 采用共同叶时钟，默认持续运行。来源可以继续接到 Two-Bone、Aim 或另一个具体 GPU 来源步骤。

移动阶段在普通 Game 更新中写入 `GpuPoseTarget.Movement`，其 `GpuPoseMovement` 包含：

| 字段 | 含义 |
| --- | --- |
| `Velocity` | 实际角色速度，单位为模型空间距离/游戏秒 |
| `AnimationVelocity` | 当前来源动作所代表的速度，采用相同坐标和时间单位 |
| `FacingDirection` | 躯干期望朝向，采用共同模型空间 |
| `Weight` | 当前运算的影响权重，范围为 `[0,1]` |

`Up` 的垂直分量从两个速度和 facing 中投影出去。`AnimationVelocity` 的平面速度为正；它可以由选定动作的根位移按相同时间间隔换算，原地片段可以由调用方提供其代表速度。两个速度可以随选定动作和当前运动分别更新。实际速度为零时沿用来源方向，并使用配置的最小比例。

角色的世界变换由移动阶段持有。调用方将世界速度、朝向和接触点转换到同一角色模型空间，再交付这些动画输入。

每条腿继续使用既有 `GpuPoseGoal`。`PositionWeight` 表示该接触目标对自由步幅目标的保持权重；`OrientationWeight` 表示接触朝向的保持权重。自由脚的两个权重为零，其位置来自步幅，朝向保留旋转后的来源脚掌。`BendDirection` 始终采用共同模型空间，直接交给 [Two-Bone](GPU-IK.md)。

## GPU 运算

设来源平面速度为 `A`，实际平面速度为 `V`，上方向为 `U`。步幅比例为 `length(V)/length(A)`，限制到 `MinimumScale..MaximumScale`。从来源方向到实际方向的有符号角以 `U` 为轴，限制到 `MaximumAngle` 后按 `Weight` 旋转骨盆及其后代。

旋转后的来源脚位置为 `C`，骨盆位置为 `P`，旋转后的来源行进轴为 `D`。自由目标为：

```text
Ffree = C + D * dot(C-P, D) * (scale-1)
Fcontact = lerp(Ffree, contact.Position, contact.PositionWeight)
F = lerp(C, Fcontact, Movement.Weight)
```

行进轴之外的来源脚间距保持原值。接触朝向从旋转后的来源脚掌向输入朝向插值，权重为 `contact.OrientationWeight * Movement.Weight`。

每条腿从当前 G 取得实际两段长度。两段长度和配置弯曲角给出近、远半径，沿 `Up` 将它们转换成该腿允许的骨盆位移区间；所有腿取交集，并限制到 `MaximumPelvisAdjustment`。脚目标相对来源脚的平均竖直位移提供期望偏移，区间内的结果平移骨盆及后代。可达区间的定义与 [Foot Locking](Foot-Locking.md#骨盆可达高度) 相同。

`WarpStride` 将每条腿的最终位置和朝向直接交给 `SolveTwoBoneGoal`；它与普通 `SolveTwoBone` 入口使用同一求解函数和骨长、弯曲限制、后代范围。腿在配置中采用各自独立的分支。目标和补偿限值共同决定最终可达距离。

最后，从来源 G 中的 `FacingAxis` 取得当前躯干朝向；它到 `FacingDirection` 的角与骨盆已转角之差，按 `GpuStrideSpine.Weight` 分配到脊柱。每项围绕当前关节位置旋转其后代，形成配置的躯干 facing。来源的行进方向和身体朝向分别参与这两个计算，因此侧移来源也采用同一输入形状。

## 完整数据链

```text
移动阶段的当前速度/方向 + 当前接触
  → GpuPoseTarget.Movement / GpuPoseGoal
  → SkinRenderExtractor 的同次 Game-world 读取窗口
  → RenderBoneSectionData
  → SkinningFeature 的当帧连续输入
  → 来源采样与 FK / 可选 ModelOffset
  → WarpStride：骨盆方向、脚目标、共同高度、Two-Bone、脊柱
  → StorePoseHistory / CompletePass
  → 变形边界 / Cluster / 用户蒙皮 shader
```

安装及显式来源关系变化时，Rig 步骤解析为当前区段的骨索引、目标编号和后代数组，并写入既有定义资源。选骨及 LOD 同时纳入骨盆、双腿、spine 和必要祖先。每帧只更新 Movement 和接触输入；运算直接写入既有当前 G。CPU 同步范围继续由显式 CPU 来源交付，GPU 后代列表沿用其写入范围规则。

每个新输入按原 G 求值条件执行一次；同次输入的多视图消费复用结果。局部来源降频时，当前移动输入仍作用于本次呈现姿势。历史保存最终调整的 G，下一输入的 previous 对应前次结果；公开异步读回沿 [GPU Pose Readback](GPU-Pose-Readback.md) 返回该结果的时刻和身份。

## 接入例子

以下是一个双腿、两节脊柱的配置例子。`source`、骨编号和腿链由调用方按所用骨架建立；`modelVelocity`、`modelAnimationVelocity`、`modelFacing` 和两个接触目标来自当前游戏更新。

```csharp
var rig = new GpuStrideRig(pelvisBone, Vector3.UnitY,
    [new(leftLeg, 0), new(rightLeg, 1)],
    [new(lowerSpine, .5f), new(upperSpine, .5f)]);
var stride = new GpuStridePose(source, rig);
GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, skeletonInstance);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuStrideParameters, GpuStrideLeaf>(target, new(stride));
Entity animation = builder.Play();

GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
input.Movement = new(modelVelocity, modelAnimationVelocity, modelFacing);
world.Replace(target, input);
GpuPoseGoal[] contacts = [leftContact, rightContact];
world.ExecuteBufferWrite<GpuPoseGoal, GpuPoseGoal[]>(target, ref contacts,
    static (goals, ref contacts) => contacts.AsSpan().CopyTo(goals.AsSpan()));
```

公开应用见 [GpuStrideRenderingTests](../../../tests/SomeEngine.Runtime.Tests/GpuStrideRenderingTests.cs)。其中单位骨长与手算三角形属于验证夹具：两个实例改变速度及方向，分别保持左右接触脚；读回当前/前帧 G，并将手算末端变换烘焙为独立可见网格参考。
