# GPU State Machines

`GpuPoseStateMachine` 保存共享状态与过渡定义，`GpuStateLeaf` 通过共同动画图提供时间与控制输入。每个目标的当前状态、来源状态、两份播放时间及过渡进度保存在 GPU 播放区段中。状态选择和双片段采样由 GPU 完成，模型空间 G 沿 [GPU Clips](GPU-Clips.md) 的层级传播与蒙皮入口交付。

## 状态、过渡与控制

`GpuPoseState` 引用一个就绪 `QuantizedClipData`，并指定循环与播放速度。`GpuPoseTransition` 指定来源状态、目标状态、控制值索引、比较方式、阈值和交叉过渡时长。比较支持相等、不同、大小及包含相等的大小关系。

定义复制状态与过渡数组，按来源状态建立连续过渡范围，并保留同一来源内的声明顺序。`InputCount` 由过渡使用的控制索引确定。各 `GpuStateInstance.Controls` 分别保存实例的浮点控制值；提取窗口把它们复制进 Rendering world 的独立数组。

每个新输入先检查当前状态的候选过渡，采用第一个条件成立的项，再推进本次时间间隔。交叉过渡期间，来源与目标分别按各自速度推进片段时间，目标权重随过渡时间线性增加；过渡完成后继续检查新候选。时长为零的过渡直接进入目标状态。循环状态将播放时间折回片段范围，有限片段在结束样本处保持。

## 共同生命周期与 GPU 执行

`GpuStateParameters.Duration` 指定共同叶节点的持续时间，默认持续运行。有限持续时间沿共同 `MotionBoundary` 与 `AnimationGraph.Submit` 完成；最后一次活动更新交付最终时间，后续更新保持该状态的播放时间。

控制携带 `AnimationGraph.ReadTime` 提供的真实叶启动次数，以及 `GpuPoseTarget.InputNumber` 和共同叶时间。GPU 播放行保存已消费的启动次数与输入编号，同一输入被多个渲染消费时保持一次状态推进。重新启动、共享定义替换和叶时间重新开始时建立初始状态。

`GpuStateSystem` 在共同时间查询内借用一次 `ComponentWriteBatch<GpuPoseTarget>`，按每个叶
实例的目标身份直接写入当前组件根。整批共享 ECS 写版本；叶时间、结束反馈和控制数组继续由
同一查询行及 `GpuStateInstance` 提供，目标组件随后通过普通渲染提取窗口交付。
该查询通过 `ReadWriteRows<Timeline,GpuStateInstance>()` 在块边界绑定列，各选中行的叶实例共同借用一次 Timeline，供共同时钟读取与完成反馈使用。

共享定义变化时装入片段与紧凑状态表；逐帧变化更新控制值和输入时钟。持久推进 pass 先更新各实例状态，再由既有采样 pass 混合来源与目标的局部 TRS 并进行 FK。平移、缩放使用线性混合，旋转使用最短路径球面插值。区段重新排列时，GPU copy 同时迁移模型历史与播放行。

## 观察结果

`SceneEdit.ReadGpuPose` 和 `SkinningFeature.RequestReadback` 将当前、上一帧 G 与对应 GPU 播放行一起复制，在 GPU 完成后交付。[GPU Pose Readback](GPU-Pose-Readback.md) 提供 Game 侧选骨请求及保留执行身份的反馈。

| 公开结果 | 含义 |
| --- | --- |
| `GpuPoseReadback.SampleTime` | GPU 实际选择的目标片段样本时间 |
| `Playback.State`、`Playback.SourceState` | 目标状态与交叉过渡来源状态 |
| `Playback.Time`、`Playback.SourceTime` | 各状态按自身速度推进的累计播放时间 |
| `Playback.SourceSampleTime` | 来源片段实际使用的循环或结束样本时间 |
| `Playback.TransitionWeight` | 目标状态的混合权重，来源权重为其余部分 |
| `Playback.Finished` | 目标有限片段已结束，且交叉过渡已完成 |
| `Starts`、`InputNumber`、`SourceCalculationTime` | 该 GPU 播放行实际消费的输入标记与计算时钟 |
| `Version`、`CalculationTime` | 包含当前父模型依赖的显示 G 版本与计算时钟 |

过渡完成后，来源状态和来源时间与目标一致，目标权重为 `1`。直接 GPU 片段、GPU 局部姿势组合及 CPU 姿势区段的状态编号为 `-1`。`RenderPoseSection.InputTime` 表示提取的控制时钟；实际片段时间由 GPU 结果提供。

## 共同图调用

下面是使用一个控制值在待机和行走之间切换的调用示例。`idle` 与 `walk` 是调用方加载的量化片段，控制索引 `0` 是此示例选择的输入。

```csharp
var machine = new GpuPoseStateMachine(
    [new(idle, Loop: true), new(walk, Loop: true)],
    [new(0, 1, 0, GpuPoseComparison.Greater, .5f, .2f),
     new(1, 0, 0, GpuPoseComparison.LessOrEqual, .5f, .1f)]);

GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, skeleton);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuStateParameters, GpuStateLeaf>(target, new(machine));
Entity animation = builder.Play();

float speed = 1;
world.ExecuteBufferWrite<GpuStateInstance, float>(animation, ref speed,
    static (instances, ref speed) => instances[0].Controls[0] = speed);
```

同一编译图可以播放到多个目标，各实例保留自己的控制值与 GPU 播放行。

源码：[共享定义](../../../src/SomeEngine.Animation.Pose/GpuPoseStateMachine.cs)、[叶节点](../../../src/SomeEngine.Animation.Pose/GpuStateLeaf.cs)、[状态表](../../../src/SomeEngine.Render.Features/Skinning/GpuStateTables.cs)、[GPU 推进与采样](../../../assets/Shaders/gpu_pose.slang)、[提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[缓冲与执行顺序](../../../src/SomeEngine.Render.Features/Skinning/SkinningFeature.cs)、[结果读回](../../../src/SomeEngine.Render.Features/Skinning/GpuPoseReadback.cs)。

[公共应用调用用例](../../../tests/SomeEngine.Runtime.Tests/GpuStateMachineRenderingTests.cs) 使用同一编译图和状态定义驱动两个角色，通过各自的控制值切换动作，读取片段时间、过渡权重和当前/上一份 G，并以 CPU 采样生成相同姿势的参考网格。用例中的两根骨骼、四分之一秒步长与两状态控制表是行为检查夹具。
