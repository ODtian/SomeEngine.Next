# Pose Graphs

姿势节点通过 `AnimationBuilder.Leaf<TParameters, TLeaf>` 安装，沿共同 Timeline、图权重和 Composition 阶段推进。编译图保存片段及参数，`ClipInstance` buffer 保存每个安装实例的 Placement、结果引用和播放历史。

## 片段播放

以下是播放一个片段的使用例子：

```csharp
var poses = new PoseStorage();
using SkeletonInstance skeleton = poses.CreateInstance(clip.Skeleton);
Entity target = PoseAnimation.CreateTarget(world, skeleton);
using var builder = new AnimationBuilder(world);
builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null,
    new ClipParameters(clip, new PoseResultId(0)));
using CompiledGraph graph = builder.Compile();
Entity animation = graph.Play(world, target);

var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
PoseAnimation.AddSystems(systems);
```

对多个目标调用同一 `CompiledGraph.Play` 可以共享定义，并分别使用 `PropertyAnimation.SetSpeed`、`Pause`、`Resume` 和 `Stop` 控制时间。系统更新完成后，`SkeletonInstance.ModelPose` 提供本次模型空间姿势、版本、计算时刻 `Time` 和局部输入采样时刻 `SampleTime`。

`ClipParameters.Loop` 选择持续循环；默认播放一次并以片段时长结束。`PoseResult.Time` 记录叶节点的采样时间，循环片段使用累计时间；片段的 `PoseResult.Phase` 是该时间除以片段时长得到的累计循环数。跨越阶段终点的更新会先向前一阶段的叶节点提供终点及剩余增量，再向后一阶段提供本阶段时间；最终姿势发布后，共同完成阶段处理节点结束。

`PoseCompositionSystem` 从零开始累加 `ImmediateSystemContext.DeltaTime`，作为该动画执行组合的共同计算时钟。模型姿势的 `Time` 记录本次 FK 计算时刻，`SampleTime` 保留所选 `PoseResult.Time`。不同播放速度和阶段分别影响来源采样时刻；父姿势驱动子骨架重新计算时，子骨架取得本次计算时刻并保留已有局部输入的采样时刻。区段读取与继承关系见 [Skeleton Inheritance](Skeleton-Inheritance.md)。

## 事件与根运动

片段标记写入动画实体的 `TimelineEvent` buffer，`Count` 是累计经过该标记的次数。一次更新可以跨越多个循环，各次经过都计入计数。

`RootMotionMode.SourcePose` 保留来源根姿势。`RootMotionMode.Extract` 根据片段选定的运动骨骼计算模型空间刚体运动，将其逆变换应用到骨架根以产生原地姿势，并将平移与旋转增量写入 `PoseResult.RootMotion`。循环增量延续各圈的完整位移和转向，移动阶段消费该结果。

以下是选择提取策略并应用当帧增量的使用例子：

```csharp
builder.Leaf<ClipParameters, ClipLeaf>(target,
    new ClipParameters(clip, new PoseResultId(0), Loop: true,
        RootMotion: RootMotionMode.Extract));
PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(0));
// 动画系统更新后，由移动阶段应用当帧增量。
PoseResult frame = PoseAnimation.ReadResult(world, in output);
actorTransform = frame.RootMotion.Transform * actorTransform;
```

变换乘法沿 `System.Numerics` 的行向量约定。`PoseFrameSystem` 在每帧开始将运动增量设为单位变换，并将当前贡献权重设为零；生产系统写入本帧贡献。结果的姿势、采样时刻与版本持续有效，完成后的骨架姿势保留在 `SkeletonInstance`。

[Motion Warping](Motion-Warping.md) 将来源根轨迹在指定窗口内对齐各角色的交互目标，通过相同 `PoseResult.RootMotion` 输出移动，并提供独立于播放历史的只读轨迹查询。

[Inertialization](Inertialization.md) 保存动作切换时的可见姿势与速度差，通过共同结果和依赖节点组衰减到当前来源。

[Physics Pose Blending](Physics-Pose-Blending.md) 将已完成刚体世界变换还原为骨骼局部姿势，按部位遮罩和共同层权重与当前动画混合，并沿普通结果恢复到动作。

## 结果交接

`PoseResultId` 表达图内的结果编号。`PoseAnimation.CreateTarget` 的 `resultCount` 分配连续的 `LocalPose` buffer 区段；`output` 选择骨架最终消费的编号。`PoseResult` buffer 保存各区段的采样时刻、累计相位、启动标记、当前贡献权重、运动增量和版本。片段贡献权重采用共同图权重乘目标上的动画层权重。

自定义叶节点在 `Install` 中调用 `PoseAnimation.Resolve(world, target, id)`，将定义中的编号解析成 `PoseResultReference`，并随自身的定义、参数索引和 Placement 保存在算法实例 buffer 中。引用中的 `Entity`、`Index` 和 `Range` 直接定位已经安装的结果。

具体系统以公开 World buffer API 读取或写入该区段。`LocalPose.Transforms` 将对应区段作为连续的 `BoneTransform` 视图交给算法。生产者完成输出后更新 `PoseResult.Time`、`Starts`、`Weight` 与 `Version`，消费者使用已解析的输入引用取得本次数据。

通过 `PoseAnimation.AddSystems(systems, addNodes)` 在片段采样后、最终骨架输出前注册按每帧运行的具体系统。以下是注册生产者和消费者系统的使用例子：

```csharp
PoseAnimation.AddSystems(systems, static composition =>
{
    AnimationSystems.Add(composition, new SourceSystem(), AnimationPhase.Composition);
    AnimationSystems.Add(composition, new ConsumerSystem(), AnimationPhase.Composition);
});
```

回调在创建执行组合时调用，保留各系统的具体类型和注册顺序。每帧依次执行 `PoseFrameSystem`、`ClipSystem`、调用方的每帧系统、依赖节点组和 `PoseCompositionSystem`。节点安装时同时连接其局部结果与 `ModelBones` 所需的父模型来源；每个依赖层之前完成就绪的父 G，再执行本层具体节点。最终系统将变化的局部结果交给对应骨架实例，再沿已绑定父实例完成父先子后的 FK；父结果版本变化也会更新保持原局部输入的子实例。随后共同图和时间线处理完成。同步选骨与 GPU 剩余范围的完成点见 [CPU/GPU Pose Handoff](CPU-GPU-Pose-Handoff.md)。

## 按结果依赖求值

Layer、Blend、Warp 以及用户后处理通过 `nodes` 回调加入 `SystemGroup<PoseNodeContext>`。叶节点在 `Install` 中调用 `PoseAnimation.RegisterNode(world, animation, output, inputs, in placement)`，保存返回的 `PoseNode`；在 `Complete` 中调用 `PoseAnimation.CompleteNodes`，在 `Release` 中调用 `PoseAnimation.ReleaseNodes`。共同图先安装全部叶节点，再进入 Complete，已解析的结果引用在这个边界编译为依赖层级。关系调整通过重新安装图建立新的连接表。

具体系统实现 `ISystem<PoseNodeContext>`，从 `context.Frame` 取得当前 World 与帧信息，处理自身 `PoseNode.Level == context.Level` 的实例。当前层全部系统完成后进入下一层；同一类型保留自己的批次、Job 与工作区，生产者在返回前完成工作并写入公开结果。用户类型沿相同的具体系统注册入口参与调度。

以下是将 Blend Space 来源和多个消费节点放入同一执行组合的使用例子：

```csharp
PoseAnimation.AddSystems(systems,
    addNodes: static composition =>
        AnimationSystems.Add(composition, new BlendSpaceSystem(), AnimationPhase.Composition),
    nodes: static nodes =>
    {
        nodes.Add(new PoseLayerSystem());
        nodes.Add(new PoseBlendSystem());
        nodes.Add(new MotionWarpSystem());
    });
```

图内输入输出决定 Layer、Blend 与 Warp 的处理层级，具体系统在对应层完成各实例的批次。

片段采样 Job 显式声明就绪片段的读取和工作区数组的写入，完成句柄在结果复制进 ECS buffer 前收敛。每个目标实体持有自身的结果 buffer；调用方持有并释放 `SkeletonInstance`。
