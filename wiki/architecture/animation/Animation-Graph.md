# Animation Graph

## Definition
动画图在 `SomeEngine.Animation` 中实现，描述时间上的组合与混合关系，编译为共享的图数据和实例上的时间、参数、轨道采样状态。固定区间和开放阶段融合进同一个实体。

```csharp
using var g = new AnimationBuilder(world);
using (g.Sequence())
{
    g.To(panel, OffsetChannels.Y, 0f, Spec.Tween(0.3f));
    g.Delay(0.1f);
    using (g.Parallel())
    {
        g.To(title, SurfaceChannels.Fill, accent, Spec.Tween(0.2f));
        g.To(icon, TransformChannels.Scale, 1f, Spec.Spring());
    }
}
Entity animation = g.Play();
```

## Invariants
- 构建器使用 `using` 作用域和池化内存。
- 构建器原语为完整泛型 `To`、`By`、`Leaf<TParameters,TLeaf>`、`Delay`、`Sequence`、`Parallel`、`Stagger`、`Repeat`、`Branch`、`Boundary`、`StateMachine`、`Blend1D`、`Layer`。组合节点是接收构建器、调用这些原语的普通函数。内置属性叶和消费者叶使用同一个公开安装接口。
- 构建器的默认容器、状态、混合样本、层和分支按并行组合子节点；`Sequence` 顺序组合，`Repeat(count)` 顺序重复其并行子节点。`Stagger(interval)` 按声明序号偏移每个子节点的起点。
- 编译规则：
  1. 固定时长的节点展开为同一条时间线上的绝对区间。
  2. 开放时长的节点（弹簧、外部推进、状态机条件）成为阶段边界，仍留在同一个实体中。
     并行组合内具有开放边界的子序列，各自保留阶段时间；它们的轨道和阶段状态仍存于该实体。一个子序列的弹簧收敛后继续自己的后续区间，整个并行组合等待全部子序列完成。
  3. 只有以下三种情况才拆成多个实体：
     - 用户显式要求某个分支有独立句柄（`Branch`）；
     - 想内联到的目标正在运行另一张图，且新图不是替换它；
     - 轨道数超出容量阈值。
- 放置：所有输出都写同一个实体时，构建器声明所需存储并内联在该实体上；多目标图使用池化的融合实体。内联目标已有启用的时间线时，新图取池化实体并保留各自的播放控制。完成时增加 `Timeline.Completions` 并禁用时间线，空闲实体通过 ECS 查询复用。
- `AnimationBuilder.TrackCapacity` 为 64。编译按目标、字段、层和图内混合关系划分输出组；容量拆分保持同一输出组完整，单个组超过阈值时使用轨道缓冲的扩展容量。容量拆分的各实体跟随原时间线的时钟、阶段、速度和暂停，并合并收敛结果；显式 `Branch` 保留独立时间线。
- `BuilderScope.Id` 标识显式分支，播放后通过 `AnimationGraph.GetBranch(world, animation, id)` 得到句柄。`GraphBranch` 缓冲保存分支句柄，正编号为构建作用域，负编号为容量扩展。`PropertyAnimation` 的暂停、恢复、停止与速度入口作用于句柄。
- 图数据的归属：
  - `Compile()` 返回可释放的 `CompiledGraph` 句柄，后续构建重新使用构建器；`CompiledGraph.Play(world, target)` 为目标创建独立实例。`target` 有值时，实例的全部输出绑定到该目标；省略时使用构建时的目标。
  - 编译数据在多个实例之间共享。每个实例持有自己的轨道采样状态、时间、参数与混合状态；固定图安装完值轨道后释放编译引用，具有图内控制的实例保留编译数据引用。
  - `CompiledGraph.Dispose()` 释放构建方的引用，已播放的实例继续使用自己的引用。句柄的值拷贝共享释放状态。`GraphSystem` 在时间线禁用、实体进入清理或系统组释放时归还实例引用；最后一个引用归还全部池化数组。
  - 实体销毁时，图系统通过 World 的延迟结构写入入口移除清理数据；调用方在帧的结构提交边界执行 `world.Flush()`，完成实体清理。
- 图参数（混合坐标、状态机条件）存于实例组件 `GraphParameters`，唯一写入方是驱动该图的游戏或界面代码。`StateMachine(ref parameters)` 和 `Blend1D(parameter, ref parameters)` 把初始参数复制到编译图；播放后驱动方用公开 World 写入入口更新实例参数。
- 轨道到达自己的起点时捕获起始值；同通道的顺序区间从前一区间的采样终值继续。`By` 沿属性动画的衰减位移语义播放，叠加层读取调用方的 `BaseValue<T>`。同字段的最后到达区间提交本帧结果，图内混合先合并为一个来源，再进入目标的层合成。

## Typed leaves and definition data

`To` 和 `By` 接收 `Spec<T,TOps,TParameters,TState,TEvaluator>`，将完整组合交给公开的 `PropertyLeaf<T,TOps,TParameters,TState,TEvaluator>`。`Spec.Boundary` 提供固定区间、收敛或外部推进语义。便利 `Spec` 在构建时把内置值类型及 Curve 解析成对应的类型化叶；缓动函数地址也在此时选定。

```csharp
var move = Spec.Tween<float, FloatOps, CubicOut>(0.3f);
using var builder = new AnimationBuilder(world);
using (builder.Sequence())
{
    builder.To(panel, X, 100f, move);
    builder.To(panel, X, 0f, Spec.Spring<float, FloatOps>());
}
using CompiledGraph graph = builder.Compile();
Entity first = graph.Play(world, firstPanel);
Entity second = graph.Play(world, secondPanel);
AnimationSystems.Add(schedule, move);
AnimationSystems.Add(schedule, Spec.Spring<float, FloatOps>());
```

`IAnimationLeaf<TParameters>` 公开以下静态扩展：

| 入口 | 数据与时机 |
| --- | --- |
| `Describe(in parameters)` | 声明阶段边界 |
| `Output(in parameters)` | 声明字段与层的编译关系 |
| `Prepare(ReadOnlySpan<TParameters>)` | 编译时生成共享的只读派生数据 |
| `Install(world, animation, target, definitions, index, in placement)` | 通过具体函数指针安装本实例的数据 |
| `Complete(world, animation)` | 同类叶安装完成后建立输出范围 |
| `Release(world, animation)` | 停止或替换时释放本实例数据 |

`AnimationLeafBatch<TParameters>.Parameters` 和 `Data` 属于编译定义，多实例共享。`AnimationLeafPlacement` 给出 Stage、Start、Motion、Group、MotionStage、Starts、Layer、Output 和本具体组合 Capacity。用户叶通过公开 World 接口持有自己的类型化状态，查询通过 `AnimationGraph.WithTimeAccess(query)` 声明共同时间和完成数据访问，系统通过 `AnimationGraph.ReadTime` 读取本叶阶段时间、权重与启动次数，再通过 `AnimationGraph.Submit` 汇总运行结果。

属性叶的共享参数保存在定义数组，实际 `MotionInstance<T,TOps,TParameters,TState,TEvaluator>` 缓冲持有实例状态、值、时间与输出。`AnimationGraph.UpdateParameters(world, animation, channel, in spec)` 更新指定实例的公式参数，并调用 `TEvaluator.UpdateParameters` 保留算法历史；首次显式修改将该具体组合的参数复制到实例缓冲。固定时长和阶段类型保持编译值，时间或结构变更通过重编译并播放新定义提交。

`PropertyAnimation.Retarget` 从本实例当前来源值重定目标并延续算法状态。图中固定叶在剩余编译区间到达新目标，后续区间按原时间开始。停止调用具体算法及公开叶的释放入口，图重用时清理旧目标关系并安装新目标。

## Compiled ranges

编译阶段按实际输出组建立字段轨道范围与顺序，同一组中的算法在各自具体批次采样后，由 `GraphOutputSystem<T,TOps>` 统一提交一个来源。目标来源关系复用 `PropertySource<T>`，层混合与批次字段写回沿共同属性合成路径执行。

固定绝对区间、开放阶段、分支部分、组内活动 motion、每个状态的候选过渡范围及 Blend1D 的已排序采样点都在编译时建立。每帧检查当前部分的活动组、当前状态的候选范围；Blend1D 以二分查找确定相邻点。参数与关系由编译定义持有，实例只推进阶段、时间、输入与混合状态。

首次激活的字段起点在 ECS 查询返回后读取，随后仅采样新激活轨道；稳态继续一次具体组合查询。字段写回在查询返回后按 Channel 成批提交。根组件的读取和写入通过同一个公开 ECS 字段接口取得调度访问能力。

## Public API

```csharp
public ref struct AnimationBuilder
{
    public const int TrackCapacity = 64;
    public AnimationBuilder(World world);
    public void To<T>(Entity target, Channel<T> channel, T value, Spec spec, byte layer = 0) where T : unmanaged;
    public void By<T>(Entity target, Channel<T> channel, T delta, Spec spec, byte layer = 0) where T : unmanaged;
    public void Delay(float seconds);
    public BuilderScope Sequence();
    public BuilderScope Parallel();
    public BuilderScope Stagger(float interval);
    public BuilderScope Repeat(int count);
    public BuilderScope Branch();
    public BuilderScope Layer(byte layer, AnimationLayer definition);
    public void Boundary(StageEnd end);
    public StateMachineScope StateMachine(ref GraphParameters parameters);
    public Blend1DScope Blend1D(byte parameter, ref GraphParameters parameters);
    public Blend1DScope Blend1D(byte parameter);
    public CompiledGraph Compile();
    public Entity Play();
    public void Dispose();
}
public ref struct BuilderScope { public int Id { get; } public void Dispose(); }
public struct CompiledGraph : IDisposable { public Entity Play(World world, Entity target = default); }
public struct GraphParameters : IComponent { public ParameterArray Values; }
[InlineArray(16)] public struct ParameterArray { private float _value; }
public ref struct StateMachineScope
{
    public BuilderScope State(int id, bool initial = false);
    public void Transition(int from, int to, GraphCondition condition, float crossfade = 0);
    public void Dispose();
}
public ref struct Blend1DScope { public BuilderScope Point(float coordinate); public void Dispose(); }
```

`GraphCondition(parameter, comparison, value)` 支持等于、不等于、小于、小于等于、大于和大于等于；`GraphCondition.Equal(parameter, value)` 是常用入口。一个状态机采用显式初始状态，省略时采用声明的第一个状态。当前状态的条件按声明顺序检查，每帧采用首个匹配过渡，目标状态从自己的起点重新播放。

消费者条件实现 `IAnimationCondition<TParameters,TState>` 的 `Matches(in parameters, in GraphParameters inputs, ref state)`，并可提供 `Initialize` 与 `Release`。通过 `GraphCondition.Create<TParameters,TState,TCondition>(parameters, inputs)` 声明，`inputs` 为可选的 `QueryDefinition` 输入访问描述。条件参数属于定义，状态以具体泛型缓冲保存在实例。内置比较经 `ParameterCondition` 采用同一入口；活动状态的候选条件通过具体 managed 函数指针求值。`CompiledGraph.Inputs` 公布编译输入关系，`GraphSystem` 通过 `WithAccess` 将其访问能力加入实例查询并保持原实体匹配条件。

交叉淡入按指定时长把当前混合权重推进到目标状态；淡入期间的新过渡从当前权重继续，各来源的轨道采样状态独立。`Blend1D` 按参数坐标选择相邻样本并线性混合；超出样本坐标时采用最近端点。暂停保留状态、样本时间与权重。`Layer` 在目标上声明对应层，作用域内的轨道使用该层，遮罩与基准值沿用 [[architecture/animation/Animation-Layers|Animation Layers]]。

```csharp
var parameters = new GraphParameters();
using var g = new AnimationBuilder(world);
using (var machine = g.StateMachine(ref parameters))
{
    using (machine.State(0, initial: true)) g.To(button, Opacity, 0.6f, Spec.Tween(0));
    using (machine.State(1)) g.To(button, Opacity, 1f, Spec.Tween(0));
    machine.Transition(0, 1, GraphCondition.Equal(0, 1), crossfade: 0.2f);
    machine.Transition(1, 0, GraphCondition.Equal(0, 0), crossfade: 0.2f);
}
Entity animation = g.Play();
parameters.Values[0] = 1;
world.Replace(animation, parameters);
```

`Boundary(StageEnd.External)` 在当前区间之后等待 `Animation.Advance(world, entity)`。`Boundary(StageEnd.Settled)` 等待当前阶段的求值工作收敛，`Boundary(StageEnd.Duration)` 结束当前固定区间。阶段切换沿用 [[architecture/animation/Timeline|Timeline]] 的越界时间与收敛规则。

## Scheduling and verification

`AnimationSystems.AddDefaults` 和 `AnimationSystems.Add(schedule, spec)` 采用共同阶段装配，依次执行时间推进、`GraphSystem`、层权重求值与合成、字段求值与合成、`GraphEndSystem`、时间线收尾。完整算法组合自动加入 `TypedTrackSystem<T,TOps,TParameters,TState,TEvaluator>`；图输出在具体算法之后合并。消费者叶系统通过 `AnimationSystems.Add(schedule, system, AnimationPhase.Properties)` 接入对应阶段。`AnimationSystems.Describe(schedule)` 使用同一个装配 helper，在初始化前后显示相同的具体系统顺序。

`tests/SomeEngine.Animation.Tests/AnimationGraphTests.cs` 通过公开 API 验证手算区间、弹簧序列、并行开放阶段、内联与池化复用、独立分支、按钮状态机、交叉淡入、Blend1D、层、共享编译图、多来源叠加、容量后的统一暂停、自定义节点与值运算，以及开发检查。`TypedGraphTests` 验证跨算法顺序区间、消费者收敛、共享定义的独立时钟、实例公式更新、重定目标的固定截止时间、公开叶释放与用户条件。

`tools/SomeEngine.Animation.Checks` 在独立消费者程序及 NativeAOT 中复用完整泛型算法、公开叶节点、共享实例和条件。性能入口分别记录编译、实例化、图推进、算法求值、控制、分配和保留内存；冷期构建的对象与共享数组按实际记录，热帧按具体组合批次执行。队列编号、测量配置及原始输出沿 [[harness/Harness-Performance-Evidence|性能证据]] 保存。

本页把图节点原语细化为已实现的 `To`、`By` 与作用域，并公开参数、分支和共享实例入口。多维混合扩展沿构建器原语继续表达。

## Relations
[[architecture/animation/Timeline|Timeline]] · [[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/animation/Property-Animation|Property Animation]]
