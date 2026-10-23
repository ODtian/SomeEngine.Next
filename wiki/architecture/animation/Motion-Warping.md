# Motion Warping

`RootMotionWarp` 保存共享的来源轨迹和修正窗口。每个角色通过 `Bind(start, target)` 建立自己的 `MotionWarpInput`，包含起始世界变换、交互目标世界变换及预计算修正。`MotionWarpLeaf` 读取已绑定的姿势结果，保留来源姿势与采样元数据，并输出修正后的 `RootMotionDelta`。角色移动或物理阶段应用这个增量，持有实际角色变换的写入职责。

## 来源轨迹与角色输入

| 数据 | 持有内容 | 公开读取或更新 |
| --- | --- | --- |
| `RootMotionPath` | 按时间排列的刚体根关键帧 | `Keys`、`Duration`、`Sample(time)` |
| `RootMotionWarp` | 来源轨迹、来源时间上的 `MotionWarpWindow` | `Source`、`Window`、`Bind(start, target)` |
| `MotionWarpInput` | 一个角色的起点、目标及对应修正 | `Start`、`Target`、`Sample(time)`、`SampleWorld(time)` |
| `MotionWarpInstance` | 已解析的结果引用、公开 `Input`、独立的播放历史 | 通过 World buffer 写入当前实例的 `Input` |

`RootMotionPath` 复制调用者的 `RootMotionKey` 数组。关键帧从零时刻开始，时间严格递增，旋转采用单位四元数。查询在线性插值平移、Slerp 插值旋转后，相对首个刚体变换返回累计运动；首尾之外保持对应端点。

`RootMotionPath.FromClip(clip, sampleRate)` 预计算片段所选 `RootMotionBone` 的模型空间刚体轨迹。它使用片段的公开采样入口与相同的根运动提取计算，采样包含片段首尾。`sampleRate` 决定来源轨迹的预计算密度。调用者也可以直接提供已选定动作或混合路径的根关键帧。

窗口满足 `0 <= Start < End <= Source.Duration`，使用来源结果的秒数。起点与目标是世界空间的刚体变换。共享图保留轨迹与窗口，各次安装的公开 `Input` 保存对应角色的起点和目标。

## 累计修正与只读查询

使用 `System.Numerics` 的行向量乘法。设来源累计运动为 `P(t)`，角色起点为 `S`，目标为 `T`，窗口结束为 `b`。绑定时计算：

```text
Cend = inverse(P(b)) * T * inverse(S)
u = clamp((t - window.Start) / (window.End - window.Start), 0, 1)
w = u * u * (3 - 2 * u)
W(t) = P(t) * C(w)
```

`C(w)` 将修正平移从零线性插值到末端值，将修正旋转从单位四元数 Slerp 到末端值。`MotionWarpInput.Sample(t)` 返回 `W(t)`，`SampleWorld(t)` 返回 `W(t) * S`。窗口起点的修正为单位变换，终点满足 `W(b) * S = T`。平滑窗口权重在两端具有零导数；窗口外保持端点修正，后续帧继续来源运动增量。

这些查询只读取轨迹和角色输入。预测调用与播放使用同一累计修正，播放历史由 `MotionWarpInstance` 单独保存。

## 公共图与移动阶段

以下使用例子包含一个来源片段和一个 Warp 节点，最终姿势选择结果 `1`：

```csharp
var warp = new RootMotionWarp(RootMotionPath.FromClip(clip),
    new MotionWarpWindow(0.5f, 1.5f));
Entity target = PoseAnimation.CreateTarget(world, skeleton,
    resultCount: 2, output: new PoseResultId(1));
using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null,
        new(clip, new PoseResultId(0), RootMotion: RootMotionMode.Extract));
    builder.WarpMotion(Entity.Null,
        new(warp, new PoseResultId(0), new PoseResultId(1)));
}
using CompiledGraph graph = builder.Compile();
Entity animation = graph.Play(world, target);
MotionWarpInput input = warp.Bind(actorTransform, interactionTransform);
world.ExecuteBufferWrite<MotionWarpInstance, MotionWarpInput>(animation, ref input,
    static (instances, ref input) => instances[0].Input = input);

PoseAnimation.AddSystems(systems, static composition =>
    AnimationSystems.Add(composition, new MotionWarpSystem(), AnimationPhase.Composition));
Matrix4x4 predictedActor = input.SampleWorld(futureSourceTime);
```

安装时的初始 `Input` 对应来源轨迹。调用者在每次 `Play` 后配置角色输入，再推进系统。多个安装实例可以共享同一个图与 Warp，并分别绑定不同的起点和交互目标。

`MotionWarpLeaf` 安装时解析来源和输出 `PoseResultReference`。同类 Warp 之间的结果依赖在 `Complete` 中确定层级；`MotionWarpSystem` 按层级完成同步计算与 buffer 写入。其它类型的生产系统在 Warp 系统之前注册，例如先注册 `PoseBlendSystem`，再注册 `MotionWarpSystem`。

系统按来源 `PoseResult.Time` 查询累计运动，逐帧产生 `W(current) * inverse(W(previous))`，并复制来源的局部姿势、时间、相位和启动标记。输出贡献权重采用 Warp 叶节点及目标动画层的权重，来源当前权重为零时输出贡献为零。来源版本推进时更新姿势与根运动；启动标记变化时重新开始累计历史。节点以来源路径时长作为共同时间线的时长边界。

动画系统更新后，移动阶段消费公开结果：

```csharp
PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(1));
PoseResult result = PoseAnimation.ReadResult(world, in output);
actorTransform = result.RootMotion.Transform * actorTransform;
```

来源片段使用 `RootMotionMode.Extract` 提供原地姿势。修正节点输出的移动与角色实际变换沿这个明确的消费点交接。

源码：[来源轨迹](../../../src/SomeEngine.Animation.Pose/RootMotionPath.cs)、[窗口与角色输入](../../../src/SomeEngine.Animation.Pose/RootMotionWarp.cs)、[Leaf](../../../src/SomeEngine.Animation.Pose/MotionWarpLeaf.cs)、[系统](../../../src/SomeEngine.Animation.Pose/MotionWarpSystem.cs)。公共行为入口：[MotionWarpingBehaviorTests](../../../tests/SomeEngine.Animation.Pose.Tests/MotionWarpingBehaviorTests.cs)。

[[architecture/animation/Pose-Graphs|Pose Graphs]] · [[architecture/animation/Pose-Blending|Pose Blending]] · #305
