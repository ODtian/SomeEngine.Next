# GPU Two-Bone IK and Aim

本文记录现行公共接口和模型空间求解路径。源码入口为 [共享来源与链](../../../src/SomeEngine.Animation.Pose/GpuPoseRig.cs)、[具体叶节点](../../../src/SomeEngine.Animation.Pose/GpuRigLeaf.cs)、[提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[链与后代范围绑定](../../../src/SomeEngine.Render.Features/Skinning/GpuIkTables.cs) 和 [GPU 算法](../../../assets/Shaders/gpu_pose.slang)。

## 来源、目标与生命周期

`GpuPoseBlend`、`GpuTwoBonePose` 和 `GpuAimPose` 是共享 `GpuPoseSource`。Two-Bone 和 Aim 各自引用一个来源结果、就绪链配置及目标编号。共同图的 `GpuTwoBoneLeaf` 或 `GpuAimLeaf` 将最终来源链编译为共享 `GpuPoseRig`；每个安装实例分别持有共同叶时钟与 Blend Space 控制值。

`GpuPoseGoal` 缓冲位于 `GpuPoseAnimation.CreateTarget` 创建的目标实体。安装按实际目标编号建立缓冲，初始权重为零。调用方在普通 Game 更新中写入；提取在同一 Game-world 读取窗口复制完整目标数组及 `GpuPoseTarget.ModelOffset`，Rendering world 沿这份数据执行。

| 输入 | 坐标或含义 | 使用位置 |
| --- | --- | --- |
| `Position`、`PositionWeight` | 共同模型空间的位置与位置权重 | Two-Bone 末端位置及 Aim 目标 |
| `BendDirection` | 共同模型空间的弯曲方向 | Two-Bone 解所在平面与弯曲侧 |
| `Orientation`、`OrientationWeight` | 共同模型空间的归一化旋转与朝向权重 | Two-Bone 末端朝向 |
| `GpuPoseTarget.ModelOffset` | 共同模型空间的平移向量 | 配置的 `TranslationBone` 及其后代 |

权重采用 `[0, 1]`。位置目标与关节位置相异，弯曲方向在根到目标方向的垂直平面内具有分量。调用方先将世界目标转换为角色共同模型空间，再写入这些输入。

`GpuTwoBoneParameters` 和 `GpuAimParameters` 的 `Duration` 沿共同 `MotionBoundary`、`AnimationGraph.ReadTime` 与 `Submit` 处理，默认持续运行。`GraphParameters` 驱动来源中的 [GPU Blend Space](GPU-Blending.md)。目标与骨盆平移在当前输入的提取窗口就绪，同一输入编号的重复消费保留同一份调整结果。

## 解析双骨骼

`GpuTwoBoneChain` 的 `Root`、`Middle`、`Tip` 使用来源 `Skeleton` 的自身骨骼编号。`MinimumBend` 与 `MaximumBend` 是两个线段之间的弯曲角，单位为弧度；直链对应零。求解使用来源 G 中的实际两段长度。

位置权重先将来源末端位置插值到目标。余弦定理把弯曲角限制转换为可用目标距离，再由根到目标方向和投影后的 `BendDirection` 构造膝/肘位置。根旋转对齐上段，中间旋转对齐下段；每次旋转同时作用于该骨骼及其全部后代 G，保持线段长度。末端旋转按 `OrientationWeight` 从当前模型旋转球面插值到目标，随后传播到末端后代。

两段几何采用 [Ryan Juckett 的解析推导](https://www.ryanjuckett.com/analytic-two-bone-ik-in-2d/)。共同模型空间的平面选择、权重、角度限制、后代传播和 GPU 数据交付由本项目实现。

## Aim 与角度分配

`GpuAimChain` 按给定顺序保存 `GpuAimBone`。每个配置项包含自身骨骼编号、归一化的局部 `Axis`、关节 `Weight` 和弧度制 `MaximumAngle`。链按祖先到后代排列。

每个关节读取前一步已经调整的 G，将当前局部轴转为模型方向，再计算指向目标位置的最短旋转。旋转角先限制到 `MaximumAngle`，再乘关节权重与目标 `PositionWeight`，并传播其全部后代。脊柱、颈部与头部因此逐项参与最终朝向。

目标位置、局部 Aim 轴、权重及旋转范围的概念参考 [Unity Aim 约束文档](https://docs.unity3d.com/Packages/com.unity.animation.rigging@1.2/manual/constraints/MultiAimConstraint.html)；本页的有序链分配和 GPU 执行顺序由当前源码定义。

## 绑定与最终 G

安装和显式连接变化时，来源关系排列为依赖步骤。Rendering-world 装配将骨骼编号解析到当前 G 范围，建立包含继承区段的后代列表，并将父区段步骤排在子区段步骤之前。

执行顺序为来源采样与 FK、可选模型平移、Two-Bone/Aim 步骤、`StorePoseHistory`、`CompletePass`，随后由 Cluster 边界和蒙皮消费者读取。历史保存最终调整的 G，下一输入的 previous 对应上一份调整结果。[公开读回](GPU-Clips.md#读取-gpu-结果) 返回同次结果的 current/previous 及计算标记。

以下是两个腿部链加注视链的调用示例。`source` 是共享 `GpuPoseBlend`，`leftLeg`、`rightLeg`、`lookChain` 是调用方按骨架建立的链，`pelvisBone` 是示例选择的骨盆骨骼编号。

```csharp
var legs = new GpuTwoBonePose(new GpuTwoBonePose(source, leftLeg, 0), rightLeg, 1);
var look = new GpuAimPose(legs, lookChain, 2);
GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, instance);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuAimParameters, GpuAimLeaf>(target, new(look, TranslationBone: pelvisBone));
builder.Play();

var foot = new GpuPoseGoal
{
    Position = modelFootPosition, PositionWeight = 1,
    BendDirection = modelKneeDirection,
    Orientation = modelFootRotation, OrientationWeight = 1,
};
world.ExecuteBufferWrite<GpuPoseGoal, GpuPoseGoal>(target, ref foot,
    static (goals, ref foot) => goals[0] = foot);
GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
input.ModelOffset = modelPelvisOffset;
world.Replace(target, input);
```

最终来源为 `GpuTwoBonePose` 时，使用 `GpuTwoBoneParameters` 与 `GpuTwoBoneLeaf`。`TranslationBone` 默认值 `-1`，指定骨骼后，`ModelOffset` 在 IK 前平移该骨骼及其后代。

公共应用调用见 [GpuIkRenderingTests](../../../tests/SomeEngine.Runtime.Tests/GpuIkRenderingTests.cs)。其中两段单位骨长、左右弯曲及分配到三个关节的限制角构成手算参考，图像沿相同网格和用户蒙皮 shader 比较。
