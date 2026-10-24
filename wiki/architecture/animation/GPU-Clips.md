# Uniform GPU Clips

本文记录均匀时间量化片段、共同动画图中的 GPU 叶子，以及 Rendering world 的模型空间 G。
源码入口为 [QuantizedClipData](../../../src/SomeEngine.Animation.Pose/QuantizedClipData.cs)、
[GpuClipLeaf](../../../src/SomeEngine.Animation.Pose/GpuClipLeaf.cs)、
[SkinningFeature](../../../src/SomeEngine.Render.Features/Skinning/SkinningFeature.cs)
和 [gpu_pose.slang](../../../assets/Shaders/gpu_pose.slang)。

## 片段数据

`QuantizedClipImporter` 从 glTF 动画导入 `QuantizedClipBlock`。
`QuantizedClipSettings` 指定采样率、skin 索引和根运动节点。导入沿用 `GltfClipReader`
的骨骼对应关系，保留片段时长、事件、根骨骼和 skeleton 资产引用。

`QuantizedClipData.Encode(clip, sampleRate)` 接受 ready `AnimationClip`。
样本数为 `ceil(duration * sampleRate) + 1`，时间均匀覆盖起止点。
每根骨骼保留完整 TRS：平移和缩放使用各分量的最小值及范围，旋转使用归一化四元数。

| 数据 | 编码 | 大小 |
| --- | --- | --- |
| 每骨骼平移、缩放范围 | 四个 `Vector4` | 64 字节 |
| 每时间点、每骨骼 TRS | 平移 3、旋转 4、缩放 3 个 unsigned 16 位值 | 20 字节 |

采样定位相邻两个时间点，线性插值平移和缩放，使用最短路径球面插值旋转。
`QuantizedClipData.CreateClip(skeleton)` 提供同一编码的 CPU 采样入口；
`QuantizedClipBlock.Data` 的使用期由普通引用保持，适用于资产关闭后的播放。

## 图叶子与提取

`GpuPoseAnimation.CreateTarget` 为一个 `SkeletonInstance` 创建 `GpuPoseTarget`。
`GpuClipLeaf` 读取共同图的播放时间、开始次数和完成边界，沿共同 Timeline 的速度推进，片段参数指定循环。
`GpuPoseAnimation.AddSystems` 将目标输入更新和片段控制系统接入共同动画阶段。

| 控制字段 | 含义 |
| --- | --- |
| `Clip` | 当前均匀量化片段 |
| `SampleTime` | 当前源样本时间，已应用播放速度和循环 |
| `StateMachine` | 共享 GPU 状态与过渡定义 |
| `Blend` | 共享 GPU 片段、Blend Space 与逐骨层组合 |
| `Rig` | 已连接来源与模型空间 IK/Aim 的共享步骤 |
| `ModelOffset` | 配置骨骼及其后代的共同模型空间平移 |
| `Controls` | 各实例的浮点控制值 |
| `PlaybackTime` | 共同 GPU 叶节点的时间 |
| `Playing` | 本次更新是否推进 GPU 状态或混合相位 |
| `CalculationTime` | Game 更新累积的计算时间 |
| `Starts` | 共同时间轴的开始次数 |
| `InputNumber` | 每次 Game 更新递增的目标输入编号 |
| `CpuBones` | 显式同步来源和所需自身骨骼集合 |

以下是公共 API 接入示例，`data`、`instance`、`binding` 由调用者加载并保持有效：

```csharp
GpuPoseAnimation.AddSystems(frame.Systems.Animation);
Entity target = GpuPoseAnimation.CreateTarget(frame.World, instance);
using var builder = new AnimationBuilder(frame.World);
builder.Leaf<GpuClipParameters, GpuClipLeaf>(target, new(data, Loop: true));
builder.Play();
frame.World.Add(frame.Scene.MeshEntities[0], new MeshSkin(instance, binding));
```

`SkinRenderExtractor` 将这些控制复制到 Rendering world。父区段、自己骨骼的父索引及
source joint 映射在安装或 `SkeletonInstance.BindingVersion` 改变时解析。
GPU 目标提取 ready 片段和控制；CPU 目标按模型姿势版本复制自身 G。

## GPU 求值与历史

`SkinningFeature` 汇总所有活动区段，为每段分配连续 G 范围。量化片段、层级工作表、
joint 索引及逆绑定矩阵在装配改变时上传。每帧提交目标控制和发生变化的 CPU 区段。

GPU 先读取已提交的 current/previous 历史，再更新各区段的播放行并装入 CPU 区段，随后按拓扑层插值局部 TRS
并传播 G。每个自身骨骼读取已绑定的父 G；继承父结果和自身结果处于同一模型空间。
各角色在同一层合并调度。最终 G 通过 [Skinning](Skinning.md) 的四个资源绑定供用户材质消费。

每个区段按输入编号和开始次数识别来源推进，并按绑定、CPU 写入和父区段显示版本识别模型求值。新的模型求值令 previous 接收上次 current；
首次结果以 current 初始化 previous。重复消费相同来源及模型依赖保留同一对矩阵；保持的子来源按新的父 G 继续计算并推进模型历史。
历史推进由 RG 中实际提交的写入确认，区段重排通过 GPU copy 保留已有历史。

`RenderGpuPoseTarget.Section` 定位提取后的区段实体；该实体的 `RenderPoseSection` 给出
G 范围、GPU 播放行及 `InputTime` 控制时钟。图消费者使用 `SkinningFeature.CurrentBones`、`PreviousBones`
和 `CompletePass` 读取已完成的输出。

## 读取 GPU 结果

`SkinningFeature.RequestReadback(section, receive)` 请求下一次提交的当前及上一份自身区段 G，以及同次 GPU 播放行。
`CompleteReadbacks()` 等待对应 copy 完成并交付 `GpuPoseReadback`；返回矩阵独立于 GPU 资源，样本时间和输入标记来自实际 GPU 结果。`Version` 与 `CalculationTime` 描述模型求值，`SourceCalculationTime` 描述来源输入时钟，逐骨骼 `Calculations` 保留各写入方的时间与版本。
普通应用通过 `SceneEdit.ReadGpuPose(target, receive)` 使用这一入口，Runtime 在正常
prepare 后解析提取映射，并在帧提交后交付结果。

共享状态机、逐实例控制值与交叉过渡通过 `GpuStateLeaf` 接入，实际状态、来源时间和过渡权重见 [GPU State Machines](GPU-State-Machines.md)。

1D/2D Blend Space、遮罩和参考姿势差叠加通过 `GpuBlendLeaf` 接入，实例参数及相位规则见 [GPU Blending](GPU-Blending.md)。

解析双骨骼、末端朝向和带权重与角度限制的注视链通过具体叶节点接入，目标输入、骨盆平移与最终 G 历史见 [GPU IK](GPU-IK.md)。

ready clip 或 CPU 图输出的同步需求、完成点及同次 G 范围交接见 [CPU/GPU Pose Handoff](CPU-GPU-Pose-Handoff.md)。

公共应用示例位于 [SkinningRenderingTests](../../../tests/SomeEngine.Runtime.Tests/SkinningRenderingTests.cs)。
示例使用共享父实例、两个播放速度及每顶点两个影响；数值读取和可见几何都经过普通 App、
既有提取、RG 与 Cluster 路径。
