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
`GpuClipLeaf` 读取共同图的播放时间、开始次数和完成边界，支持播放速度及循环。
`GpuPoseAnimation.AddSystems` 将目标输入更新和片段控制系统接入共同动画阶段。

| 控制字段 | 含义 |
| --- | --- |
| `Clip` | 当前均匀量化片段 |
| `SampleTime` | 当前源样本时间，已应用播放速度和循环 |
| `CalculationTime` | Game 更新累积的计算时间 |
| `Starts` | 共同时间轴的开始次数 |
| `InputNumber` | 每次 Game 更新递增的目标输入编号 |

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

GPU 先读取已提交的 current/previous 历史，再装入 CPU 区段，随后按拓扑层插值局部 TRS
并传播 G。每个自身骨骼读取已绑定的父 G；继承父结果和自身结果处于同一模型空间。
各角色在同一层合并调度。最终 G 通过 [Skinning](Skinning.md) 的四个资源绑定供用户材质消费。

每个区段按输入编号、开始次数及绑定版本识别新输入。新输入令 previous 接收上次 current；
首次结果以 current 初始化 previous。重复渲染相同输入保留同一对矩阵。
历史推进由 RG 中实际提交的写入确认，区段重排通过 GPU copy 保留已有历史。

`RenderGpuPoseTarget.Section` 定位提取后的区段实体；该实体的 `RenderPoseSection` 给出
G 范围及输入时间信息。图消费者使用 `SkinningFeature.CurrentBones`、`PreviousBones`
和 `CompletePass` 读取已完成的输出。

## 读取 GPU 结果

`SkinningFeature.RequestReadback(section, receive)` 请求下一次提交的当前及上一份自身区段 G。
`CompleteReadbacks()` 等待对应 copy 完成并交付 `GpuPoseReadback`；返回矩阵独立于 GPU 资源。
普通应用通过 `SceneEdit.ReadGpuPose(target, receive)` 使用这一入口，Runtime 在正常
prepare 后解析提取映射，并在帧提交后交付结果。

公共应用示例位于 [SkinningRenderingTests](../../../tests/SomeEngine.Runtime.Tests/SkinningRenderingTests.cs)。
示例使用共享父实例、两个播放速度及每顶点两个影响；数值读取和可见几何都经过普通 App、
既有提取、RG 与 Cluster 路径。
