# Animation Layers

## Definition
动画层是按顺序叠加的合成单位，每层有模式、权重和遮罩。属性动画已在 `SomeEngine.Animation` 中实现；姿势动画沿用相同的层规则。

```csharp
public struct AnimationLayers : IComponent { public LayerArray Items; public byte Count; }
[InlineArray(8)] public struct LayerArray { private AnimationLayer _item; }
public struct AnimationLayer { public LayerMode Mode; public float Weight; public LayerMask Mask; }
public enum LayerMode : byte { Override, Additive }
public readonly struct LayerMask
{
    public readonly int Start, Count;
    public LayerMask(int start, int count);
    public bool AllChannels { get; }
}
[BufferCapacity(8)]
public struct LayerChannel : IBufferElement
{
    public ushort Component, Offset;
    public static LayerChannel Of<T>(Channel<T> channel) where T : unmanaged;
}
[BufferCapacity(4)]
public struct Contribution<T> : IBufferElement where T : unmanaged
{
    public Channel<T> Channel;
    public byte Layer;
    public T Value;
    public Entity Source;
    public uint Output;
}
[BufferCapacity(4)]
public struct BaseValue<T> : IBufferElement where T : unmanaged
{
    public Channel<T> Channel;
    public T Value;
}
public enum TrackFields : byte { Properties, LayerWeights }
public struct ComposeSystem<T, TOps>(TrackFields fields = TrackFields.Properties)
    : ISystem<ImmediateSystemContext>
    where T : unmanaged where TOps : struct, IValueOps<T>;
public static partial class PropertyAnimation
{
    public static void SetBase<T>(World world, Entity target, Channel<T> channel, T value) where T : unmanaged;
}
```

```text
result = 基准值
for 每个贡献（按层编号，再按来源和输出编号）:
    遮罩选中通道时:
        Override: result = TOps.Lerp(result, Value, Weight)
        Additive: result = TOps.Add(result, TOps.Scale(TOps.Subtract(Value, 基准值), Weight))
```

## Invariants
- 层定义存于目标实体上的定长组件，`Count` 选择其中的前 1–8 层。权重范围为 0–1；调用方直接修改层数据或通过通道动画权重。
- 属性遮罩引用目标的 `DynamicBuffer<LayerChannel>` 中 `[Start, Start + Count)` 的通道集合。`default(LayerMask)` 选中全部通道；显式 `new LayerMask(0, 0)` 选择空集合。多个层可以共享通道范围。姿势动画的骨骼权重表由其数据形态定义。
- `BaseValue<T>` 由调用方通过 `SetBase` 声明和更新，合成系统只读取。叠加层与部分权重的覆盖层要求该字段具有基准值；目标只配置完整权重的覆盖层时，首个贡献可以作为起始值。
- `To` 在同一目标、通道和层上复用值轨道；`By` 保留同层的独立衰减位移。叠加位移围绕调用方的基准值播放，基准值更新后保留自身位移幅度，完成时回到最新基准值。
- 单来源目标经轨道存储直接更新字段，并应用相同的权重与遮罩。共享字段首次出现时，`To` / `By` 为目标声明 `Contribution<T>`；已声明的缓冲保留在目标上，逐帧刷新内容。外部求值系统通过 `world.AddBuffer<Contribution<T>>(target)` 声明合成输入，并用公开缓冲写入入口追加贡献。
- `ComposeSystem<T, TOps>` 是与轨道系统相同的公开 struct 系统。它把贡献按 `(Component, Offset, Layer, Source.Index, Source.Generation, Output)` 排序，逐字段应用层规则，更新该字段一次，随后清空已消费的贡献并保留缓冲容量。同一字段同一层的 `(Source, Output)` 唯一；不同层可以使用相同来源编号。轨道系统使用动画实体与轨道输出编号，外部系统填写自己的编号。暂停轨道持续提交其当前采样值，使其它来源可以继续推进。
- 相同层的多个覆盖贡献按来源顺序分别混合；完整权重时最后一个来源覆盖先前结果。叠加贡献按同样的来源顺序加和，浮点求和顺序也因此确定。
- 一帧跨过多个固定时长阶段时，轨道按阶段边界推进采样状态；同一时间线、目标、通道和层由最后到达的对应阶段提交字段值或贡献。其它通道和层分别保留自身到达的值。单来源直接更新字段一次，多来源把最终阶段的贡献交给合成系统，随后由时间线收尾推进阶段与完成计数。
- 字段求值与字段合成分别形成阶段，各阶段内的求值系统可以调整顺序。层权重作为控制数据先经过 `TrackFields.LayerWeights` 求值与合成，再处理 `TrackFields.Properties`；权重通道应用完整权重的覆盖规则。所有值类型均读取本帧更新后的层权重。
- 普通 `To` 值轨道完成后，其最终采样值继续由原轨道持有，参与该层随后变化的权重与遮罩合成。
  时间线的完成与层值的保留分别表达播放结束和合成输入；权重动画可以在值轨道完成前或完成后启动。
  内联时间线重新启动时保留其它字段的结束层值；池化实体持有这类值期间供同一字段重定目标复用，
  其它字段使用可复用的空闲实体。位移完成后继续按基准值归零的规则处理。
  求值系统通过 ECS 的启用、禁用查询筛选播放状态，两类轨道共用同一求值入口。
- 使用错误由 Core `DevelopmentCheck` 报告，相关调用与参数求值在 shipping 调用方中消除。
- 状态机的交叉淡入在图内部用两个分支加权实现；作用于整个目标的叠加效果（悬停、上半身覆盖、呼吸、IK 修正）使用层。

## Weight channels and scheduling

```csharp
static readonly Channel<float> HoverWeight = Channel<float>.Of<AnimationLayers>(
    static (ref AnimationLayers layers) => ref layers.Items[1].Weight);

var schedule = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
schedule.Add(new TimelineSystem());
schedule.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new TrackSystem<MyValue, MyOps>());
schedule.Add(new ComposeSystem<MyValue, MyOps>());
schedule.Add(new TimelineEndSystem());
```

有多个值类型时，先加入它们的全部求值系统，再加入对应的字段合成系统。`AnimationSystems.AddDefaults` 按这一顺序组合内置类型。外部权重求值系统放在权重合成之前，外部字段求值系统放在字段合成之前。

## Verification

`tests/SomeEngine.Animation.Tests/AnimationLayersTests.cs` 经公开 World 帧驱动覆盖三层合成、权重淡入、同层多位移与最新基准值、单来源存储、通道遮罩、外部贡献顺序、暂停与重定目标、跨类型权重更新、自定义值运算、默认系统组合、跨阶段的最后字段与权重，以及来源编号开发检查。

`PropertyAnimationTests.SteadyDefaultAnimationFramesAllocateZeroManagedBytes` 用完整 `AnimationSystems.AddDefaults` 帧预热 512 次并采样 128 次，覆盖内联、池化与八个并发叠加位移；托管分配为 0 字节。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/animation/Pose-Animation|Pose Animation]] · [[architecture/animation/Animation-Graph|Animation Graph]]
