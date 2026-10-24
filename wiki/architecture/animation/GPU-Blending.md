# GPU Blend Spaces and Layers

`GpuPoseBlend` 保存共享的局部姿势组合，`GpuBlendLeaf` 将共同图的时间与各实例 `GraphParameters` 提取到 Rendering world。GPU 计算 Blend Space 的片段选择、累计相位、TRS 混合及逐骨层，随后沿 [GPU Clips](GPU-Clips.md) 的分层 FK、姿势历史和 Cluster 蒙皮入口产生模型空间 G。

本页记录现行公共接口与执行路径。源码入口为 [共享组合](../../../src/SomeEngine.Animation.Pose/GpuPoseBlend.cs)、[共同叶节点](../../../src/SomeEngine.Animation.Pose/GpuBlendLeaf.cs)、[GPU 数据表](../../../src/SomeEngine.Render.Features/Skinning/GpuBlendTables.cs) 和 [GPU 运算](../../../assets/Shaders/gpu_pose.slang)。

## 共享数据与实例输入

| 类型 | 共享数据 | 实例输入与历史 |
| --- | --- | --- |
| `GpuBlendClip` | 就绪 `QuantizedClipData`、循环与速度 | 累计播放时间 |
| `GpuBlendSpace` | 既有 `BlendSpace`、各点的量化片段、坐标参数索引 | 坐标值、累计循环相位及当前贡献片段 |
| `GpuBlendLayer` | 基础节点、来源节点、就绪 `BoneMask`、`LayerMode`、权重和可选权重参数索引 | 本帧权重参数值 |

`GpuBlendSpace` 在构造时按 `sampleRate` 对其来源片段进行均匀量化，保留 `BlendSpace` 的坐标、区间、三角形与相位偏移。`Clips` 提供这些就绪量化数据，可通过 `CreateClip` 取得相同编码的 CPU 采样入口。

`GpuPoseBlend(skeleton, output)` 沿输出节点的基础与来源关系排列共享步骤，并将引用解析为步骤编号。同一个节点引用只保存一份步骤。每个安装实例拥有自己的控制数组；Game world 的共同 CompositionInputs 阶段在属性最终合成之后将当前 `GraphParameters` 复制到该数组，提取窗口再复制到 Rendering world。

## 选择、相位与逐骨合成

1D 区间、2D 三角剖分、范围外最近边与累计相位遵循 [Pose Blending](Pose-Blending.md) 的公开规则。每个 Blend Space 节点独立保持累计循环数，按本次叶时间间隔及当前混合频率推进；贡献片段使用共同相位与各点的 `PhaseOffset`。坐标改变从已有相位继续。

`GpuBlendLayer` 使用 [Pose Layers](Pose-Layers.md) 的 Override 与参考姿势 Additive 公式。有效逐骨权重是固定 `Weight`、可选 `GraphParameters[WeightParameter]` 和 `Mask.Weights[bone]` 的乘积。局部步骤完整贡献自身结果，层操作按其指定权重组合基础与来源。

GPU 的输入推进 pass 为每个节点计算本帧贡献及相位。同一骨骼的采样 invocation 按已解析的步骤顺序计算局部 TRS，基础和来源从该 invocation 已写入的连续工作区读取；最终局部变换参与既有 FK。各角色的参数与工作区范围独立。

## 共同生命周期与使用

`GpuBlendParameters.Duration` 使用共同叶节点的持续时间，默认持续运行。有限持续时间在共同完成阶段交付最终姿势。启动次数、输入编号与叶时钟沿用 [GPU State Machines](GPU-State-Machines.md) 的输入识别；每个新输入推进一次。相同来源及模型依赖的重复消费保留同一份 current/previous G；父显示版本变化时沿 [CPU/GPU Pose Handoff](CPU-GPU-Pose-Handoff.md) 的依赖规则重算 G。区段重新排列时复制相位历史，替换组合定义或重新启动时从初始相位求值。

下面是用速度选择移动片段，再按上身遮罩叠加动作的调用示例。`walk`、`run` 是调用方加载的 `AnimationClip`，`gesture` 是就绪 `QuantizedClipData`，`skeleton` 是 `SkeletonInstance`，`upperBody` 是就绪遮罩。

```csharp
var movement = new GpuBlendSpace(BlendSpace.From1D([
    new BlendPoint1D(1, walk), new BlendPoint1D(4, run),
]), parameterX: 0);
var pose = new GpuPoseBlend(skeleton.Skeleton,
    new GpuBlendLayer(movement, new GpuBlendClip(gesture, Loop: true), upperBody,
        LayerMode.Additive, WeightParameter: 1));

GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, skeleton);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuBlendParameters, GpuBlendLeaf>(target, new(pose));
Entity animation = builder.Play();
var inputs = new GraphParameters();
inputs.Values[0] = 2;
inputs.Values[1] = .5f;
world.Replace(animation, inputs);
```

二维空间使用既有 `BlendSpace.From2D`，传入 `GpuBlendSpace(space, parameterX, parameterY)`。同一 `CompiledGraph` 播放到多个目标时，每个实例分别写入自己的 `GraphParameters`。

`SceneEdit.ReadGpuPose` 读取当前及上一份 G；Blend 叶结果的 `SampleTime` 与 `Playback.Time` 表示共同叶播放时间，各来源按各自相位采样。公开应用调用用例见 [GpuBlendRenderingTests](../../../tests/SomeEngine.Runtime.Tests/GpuBlendRenderingTests.cs)，其中 CPU 参考沿既有 `BlendSpaceSystem` 与 `PoseLayerSystem` 生成相同组合的姿势，再通过同一网格与用户蒙皮 shader 呈现。
