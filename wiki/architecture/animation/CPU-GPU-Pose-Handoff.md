# CPU Critical Bones and GPU Pose Handoff

本页记录同步骨骼需求、完成顺序和共享 G 写入范围。源码入口为 [CpuBoneDemand](../../../src/SomeEngine.Animation.Pose/CpuBoneDemand.cs)、[PoseNodes](../../../src/SomeEngine.Animation.Pose/PoseNodes.cs)、[SkeletonInstance](../../../src/SomeEngine.Animation.Pose/SkeletonInstance.cs)、[提取](../../../src/SomeEngine.Render.Features/Skinning/MeshSkin.cs) 和 [SkinningFeature](../../../src/SomeEngine.Render.Features/Skinning/SkinningFeature.cs)。

## 声明来源与需求

`GpuPoseAnimation.SetCpuBones(world, target, demand)` 为既有 `GpuPoseTarget` 安装或替换同步需求。调用发生在装配时，或本次共同动画更新之前的 Game 输入阶段。`CpuBoneDemand.Bones` 使用该定义的自身骨骼索引，与 `ClipSampleSelection` 一致；`ModelPose.GetBone` 使用包含继承骨骼的模型索引。

| 构造方式 | CPU 局部来源 | 来源时间 |
| --- | --- | --- |
| `CpuBoneDemand(clip, bones)` | ready `AnimationClip`，复用 `ClipSampleBatch` 选骨采样 | 直接 GPU clip 的 `SampleTime`，或 GPU 组合的共同 `PlaybackTime`，限制到该 CPU clip 时长 |
| `CpuBoneDemand(source, bones)` | 已安装 CPU 图的 `PoseResultReference`，读取其连续 `LocalPose` 输出 | 该 `PoseResult.Time` |

所需自身祖先在安装或显式修改时展开。继承根继续要求实际父实例的对应模型骨骼；普通 GPU clip 父来源使用其量化数据的 ready CPU sampler。其它 GPU 父来源可通过空骨骼集合声明自己的 ready CPU clip 或 CPU result，供子需求展开使用。

CPU 节点的 `ModelBones` 声明模型空间算法连接的骨骼。来源依赖中的该组骨骼参与同步结果时，需求同时展开整组及其祖先。内置 FABRIK 和 PBIK 在安装时提供对应链与连接体。自定义具体叶节点可向 `PoseAnimation.RegisterNode` 传入 `modelBones`，继续使用共同节点依赖与完成路径。

以下是直接片段的公共调用示例。`instance` 与 `data` 是调用方已取得的骨架实例和量化片段。

```csharp
GpuPoseAnimation.AddSystems(systems);
Entity target = GpuPoseAnimation.CreateTarget(world, instance);
using var builder = new AnimationBuilder(world);
builder.Leaf<GpuClipParameters, GpuClipLeaf>(target, new(data, Loop: true));
builder.Play();
GpuPoseAnimation.SetCpuBones(world, target,
    new CpuBoneDemand(data.CreateClip(instance.Skeleton), [handBone]));
AnimationSystems.Add(systems, new ReadHandSystem(instance.ModelPose), AnimationPhase.Completion);
```

CPU 图来源使用 `PoseAnimation.Resolve(world, cpuTarget, resultId)` 取得连接。该 CPU target 与 GPU target 引用同一个 `SkeletonInstance`；CPU 图照常生成局部结果，同步需求负责所需 G 的发布。传入新的 `CpuBoneDemand` 显式替换来源或骨骼集合，传入 `null` 撤销该需求。`SkeletonInstance.CpuBones` 提供当前已展开的自身 CPU 写入集合；父实例分别持有自己的集合。

## 本次更新的完成顺序

共同 Properties 阶段形成 GPU 来源时钟与控制，Composition 阶段生成 CPU 局部结果。安装的节点依赖同时包含当前继承父模型来源。执行每个节点层之前，复制就绪的 CPU 局部输入，完成该层所需父 G，再执行具体求解器。父节点结果因此先于子 FABRIK/PBIK 的父矩阵读取。

同步骨骼使用保留的选骨采样器、连续局部工作区和具体 Job。父结果完成后，Job 只写本实例展开集合内的 G。`GpuPoseAnimation.AddSystems` 注册的 Completion 系统完成最终 CPU 工作及其 CPU 后代传播；随后注册的 Completion 消费者在同次 Game 更新内读取 `BonePose`。提取读取这次完成后的数据。

GPU 剩余骨骼与 CPU 同步骨骼共享既有模型 G 范围。普通提取窗口复制 CPU 矩阵及逐骨骼计算信息；Rendering world 为实际变更范围声明当帧 copy，再接入持久 FK 顺序点。GPU FK 工作表只包含剩余骨骼，读取同次已复制的父 G。模型空间后处理在 CPU 写入骨骼处结束传播，其 GPU 后代以该 CPU G 为锚。完成的 G 继续供 Cluster 蒙皮、姿势包围盒和其它视图消费。

## 计算信息与保持的来源

[Skeleton Instances](Skeleton-Instances.md) 的 `BonePose.Calculation` 与 `ModelPoseSection.Calculations` 记录每根 CPU 持有矩阵的版本、计算时间和局部来源时间。选骨写入只更新对应项。

GPU 将来源输入推进与模型求值分开。输入编号推进状态机、Blend Space 相位一次；模型求值同时依赖已安装父区段的显示版本。子来源保持时，新的父 G 仍会触发子 FK、最终历史及姿势包围盒更新。新 previous 对应上一份实际求值的 current。

| 公共 GPU 结果字段 | 含义 |
| --- | --- |
| `InputNumber`、`Starts` | 实际 GPU 来源输入及开始身份 |
| `SourceCalculationTime` | 该来源输入的计算时钟 |
| `SampleTime`、`Playback` | 实际 GPU 来源选择、来源时间与过渡信息 |
| `Version`、`CalculationTime` | 完整显示 G 的求值版本与计算时钟，包含父结果依赖 |
| `Calculations` | 每根当前矩阵的生产版本、计算时间和局部来源时间；CPU 写入项保留自己的计算信息 |

`RenderPoseSection.Version` 向 Rendering-world 消费者提供同一显示版本，姿势包围盒按该版本刷新。[GPU Clips](GPU-Clips.md) 的公开读回返回独立保留的矩阵与计算信息。

公共调用入口还见 [CriticalBoneBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/CriticalBoneBehaviorTests.cs) 和 [CriticalBoneRenderingTests](../../../tests/SomeEngine.Runtime.Tests/CriticalBoneRenderingTests.cs)。
