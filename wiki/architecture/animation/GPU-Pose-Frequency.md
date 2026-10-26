# GPU Pose Frequency and Bone LOD

本页记录现有视觉采样频率、骨骼选择、局部 TRS 呈现及同步来源信号。源码入口为 [GpuPoseEvaluation](../../../src/SomeEngine.Animation.Pose/GpuPoseEvaluation.cs)、[GPU clip 与共同时间](../../../src/SomeEngine.Animation.Pose/GpuClipLeaf.cs)、[事件与选根采样](../../../src/SomeEngine.Animation.Pose/GpuClipSignals.cs)、[提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs)、[已解析选择与时钟](../../../src/SomeEngine.Render.Features/Skinning/GpuPoseEvaluation.cs) 和 [GPU 局部采样/FK](../../../assets/Shaders/gpu_pose.slang)。

## 显式频率与骨骼选择

`GpuPoseAnimation.SetEvaluation(world, target, evaluation)` 在装配或显式修改时设置视觉需求。`GpuPoseEvaluation(interval)` 选择全部自身骨骼，`GpuPoseEvaluation(interval, bones)` 选择给定自身索引，`interval` 的单位为秒。默认需求及间隔 0 每次来源输入都采样，直接呈现当前局部结果。

以下是公共调用示例，演示每 0.1 秒采样调用方选择的自身骨骼。

```csharp
GpuPoseAnimation.SetEvaluation(world, target,
    new GpuPoseEvaluation(.1f, selectedOwnBones));
GpuPoseAnimation.SetCpuBones(world, target,
    new CpuBoneDemand(readyClip, criticalOwnBones));
```

Rendering world 在安装、骨骼绑定、需求集合或具体算法连接改变时解析需求：合并 CPU 同步集合、GPU rig 的连接链与平移根，再展开自身及继承父生产方的祖先。CPU 持有的 G 沿既有 copy 范围写入；GPU 采样其余选定骨骼。其余骨骼采用 Skeleton 的参考局部 TRS，并继续从当前父 G 执行 FK。这一规则提供完整的模型姿势与蒙皮绑定。

来源输入、状态机和 Blend Space 相位照常随每次 Game 输入推进。`InputNumber`、实际叶 `Starts`、安装 `AnimationStarts` 及当次 `Readbacks` 集合继续通过正常提取交付。视觉降频发生在 GPU 局部姿势采样，当前请求和来源身份由该次输入持有。

Game 侧 `DevicePoseSources` 与 Rendering 共用 `GpuPoseEvaluation.SelectBones`、参考姿势打包和
`GpuPosePresentation.FromSampleTimes`，沿同一需求采样和呈现局部 TRS。
接受的两端时钟、来源身份与需求引用保存在目标实体的来源状态；纯需求修改重建派生选择并重设相关局部样本。
同 World 中原物理链、CPU 关键骨和低频视觉源的组合继续使用本页公开应用夹具验收。

## 局部 TRS 呈现规则

每个实例保留最近两次实际视觉采样的局部 TRS、计算时钟和来源身份。第一份结果同时初始化两端；后续当来源计算时钟达到上次采样时钟加 `Interval`，采样该次实际输入。显式修改需求或重新开始播放时，从当前输入重新初始化两端。子节需求改变父生产节的有效选骨集合时，该父节也按当前来源重建两端，再沿既有 G、历史与边界依赖继续求值。

设两端实际计算时钟为 `a`、`b`，当前模型求值时钟为 `t`，间隔为 `h`。正间隔的呈现权重为 `clamp((t - h - a) / (b - a), 0, 1)`；两端相同时权重为 1。模型求值时钟包含当前来源、CPU 写入及继承父模型依赖的时钟。

平移与缩放采用线性插值；旋转按同半球最短弧插值。插值后的局部 TRS 先转矩阵，再从同次父 G 执行 FK。模型空间 IK/Aim 等后处理继续消费该姿势。完整显示 G 写入正常 current/previous 历史，供姿势包围盒、Cluster、阴影及其它视图使用。

正间隔因此采用一个采样间隔的呈现延迟，使用已经取得的两端结果。CPU 关键骨骼继续在当次 Completion 点交付；GPU 后代从该当前 CPU G 出发组合自己按规则呈现的局部姿势。继承父显示版本变化也触发新的 FK、历史和边界。

## 完整区间的事件与根运动

`GpuClipLeaf` 在共同 CompositionInputs 阶段读取当前叶时间，按前次到本次的完整来源区间累计 `TimelineEvent`。循环区间采用既有 `ClipSystem.CrossedEvents` 规则。

`GpuClipParameters(clip, Loop: true, RootMotion: RootMotionMode.Extract)` 启用根运动提取。共享定义保留 ready CPU clip、根及必要祖先、起点和循环变换；逐实例工作区只采这条根链。输出采用既有 `ClipRootMotion` 的相位、循环幂与前次变换语义。`GpuPoseTarget.RootMotion` 是本次 Game 更新的增量，消费者将其乘在前次角色变换之前。共同帧系统每次先初始化该增量。

根运动校正与当前输入一起提取：CPU 同步片段根在选骨 Job 内应用同次校正，GPU 视觉根在自己的实际采样点应用校正。视觉频率因此独立于事件、根运动和关键骨骼的更新。

## 公开计算信息

[GPU Pose Readback](GPU-Pose-Readback.md) 保留该次请求与实际来源身份。新增呈现信息如下。

| 字段 | 含义 |
| --- | --- |
| `Presentation.PreviousTime`、`CurrentTime` | 两端实际视觉采样的计算时钟 |
| `Presentation.PreviousInput`、`CurrentInput` | 对应两端的完整来源身份 |
| `Presentation.Time`、`Amount` | 按上述规则形成的呈现时钟及权重 |
| `SampledBones` | 本范围内已解析的 GPU 来源采样骨骼，自身索引 |
| `SourceSamples` | 每根返回骨骼的两端实际局部来源样本时间；CPU 持有项的两端为该次 CPU 来源时间，参考局部项为 0 |

`CalculationTime` 与 `Version` 继续描述完整 G 的实际求值；`SourceCalculationTime`、顶层 `SampleTime` 与 `Playback` 描述本次实际 GPU 来源状态。逐骨骼 `Calculations` 保留生产方计算信息，GPU 插值项的 `SampleTime` 为两端来源样本时间按权重的组合；`SourceSamples` 与 `Presentation` 同时保留两端事实。

公共应用示例见 [GpuPoseFrequencyTests](../../../tests/SomeEngine.Runtime.Tests/GpuPoseFrequencyTests.cs)。
