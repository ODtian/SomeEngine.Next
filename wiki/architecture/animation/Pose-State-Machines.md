# Pose State Machines

姿势状态机由共同 `StateMachine`、姿势叶节点与 `PoseBlend` 组合。编译图持有状态、候选过渡范围和条件参数；每个播放实例持有图参数、活动状态、各状态时钟、淡入进度及具体条件历史。共同规则见 [Animation Graph](Animation-Graph.md)，姿势输入与输出见 [Pose Graphs](Pose-Graphs.md) 和 [Pose Blending](Pose-Blending.md)。

源码入口为 `GraphPlayback`、`IAnimationCondition`、`ClipLeaf`、`ClipSystem`、`PoseBlendLeaf` 与 `PoseBlendSystem`。

## 图与结果组合

各状态将姿势写入独立的 `PoseResultId`。状态机外的 `PoseBlend` 读取这些结果，以各来源的当前贡献权重组合为最终结果。将状态机和混合叶放在同一个 `Parallel` 中，共同时间线会在同一次更新中推进状态、采样片段、混合姿势并计算最终模型空间姿势。

以下是从行走交叉过渡到跑步的使用例子，其中 `walk` 与 `run` 是同一骨架的就绪片段：

```csharp
var inputs = new GraphParameters();
var sources = new PoseBlend([
    new(new PoseResultId(0)),
    new(new PoseResultId(1)),
]);
using var builder = new AnimationBuilder(world);
using (builder.Parallel())
{
    using (var machine = builder.StateMachine(ref inputs))
    {
        using (machine.State(0, initial: true))
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null,
                new(walk, new PoseResultId(0), Loop: true, RootMotion: RootMotionMode.Extract));
        using (machine.State(1))
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null,
                new(run, new PoseResultId(1), Loop: true, RootMotion: RootMotionMode.Extract));
        machine.Transition(0, 1, GraphCondition.Equal(0, 1), crossfade: 0.5f);
    }
    builder.BlendPose(Entity.Null, new(sources, new PoseResultId(2), new(StageEnd.Settled)));
}
using CompiledGraph graph = builder.Compile();

Entity firstTarget = PoseAnimation.CreateTarget(world, firstSkeleton, 3, new PoseResultId(2));
Entity secondTarget = PoseAnimation.CreateTarget(world, secondSkeleton, 3, new PoseResultId(2));
Entity firstAnimation = graph.Play(world, firstTarget);
Entity secondAnimation = graph.Play(world, secondTarget);
PoseAnimation.AddSystems(systems, static composition =>
    AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));

GraphParameters firstInputs = world.Read<GraphParameters>(firstAnimation);
firstInputs.Values[0] = 1;
world.Replace(firstAnimation, firstInputs);
```

两个目标使用同一份编译定义。驱动方将参数写入各自返回的动画句柄，目标骨架分别消费各自的最终结果。状态内部也可以组合 Blend Space 与其它公开姿势叶，并将该状态的最后一个结果交给外层混合。

## 过渡中的时间、事件与移动

匹配的过渡从目标状态起点启动其独立时钟。共同图按交叉淡入进度更新各状态的来源权重，片段通过 `AnimationGraph.ReadTime` 消费这些时间和权重；`PoseBlendSystem` 随后使用本次结果。淡入期间再次过渡时，新的淡入从当前各状态权重继续。

`PoseResult.Weight` 提供当前来源贡献，最终结果的采样时间与累计相位采用 [Pose Blending](Pose-Blending.md) 的加权语义。片段标记由本帧参与采样的活动片段写入动画句柄的 `TimelineEvent` 计数。

使用 `RootMotionMode.Extract` 时，片段先产生原地姿势和当帧运动增量，混合节点按本次来源权重组合增量。移动阶段应用最终结果的一份 `RootMotion`，模型空间姿势和移动共同构成角色的世界空间姿势。读取与乘法顺序见 [Pose Graphs](Pose-Graphs.md#事件与根运动)。

## 用户条件

用户条件实现共同 `IAnimationCondition<TParameters,TState>`。参数保存在共享定义，`TState` 的值由每个实例独立持有。下面是持续按住输入后触发过渡的使用例子；驱动方把输入值写入参数 0，把此次更新的时间增量写入参数 1：

```csharp
readonly record struct HeldInputParameters(byte Input, byte Delta, float Duration);
struct HeldInputState { public float Elapsed; }
readonly struct HeldInputCondition : IAnimationCondition<HeldInputParameters, HeldInputState>
{
    public static bool Matches(in HeldInputParameters parameters, in GraphParameters inputs, ref HeldInputState state)
    {
        if (inputs.Values[parameters.Input] == 0)
        {
            state.Elapsed = 0;
            return false;
        }
        state.Elapsed += inputs.Values[parameters.Delta];
        if (state.Elapsed < parameters.Duration) return false;
        state.Elapsed = 0;
        return true;
    }
}
```

在状态机作用域中通过共同入口声明该条件：

```csharp
machine.Transition(0, 1,
    GraphCondition.Create<HeldInputParameters, HeldInputState, HeldInputCondition>(new(0, 1, 0.5f)),
    crossfade: 0.5f);
```

构图或重新编译并播放定义时维护状态与过渡关系；逐帧更新沿已有候选范围求值条件、时间与权重。
