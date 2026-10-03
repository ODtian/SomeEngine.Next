# Property Animation

## Definition
属性动画在 `SomeEngine.Animation` 中实现。通道按组件编号与字节偏移定位 unmanaged 组件字段，轨道缓冲持有值、曲线与采样状态，普通 ECS 系统完成求值。使用者包括界面、FLIP 和游戏中的各种渐变。

```csharp
public readonly struct Channel<T> : IEquatable<Channel<T>> where T : unmanaged
{
    public readonly ushort Component;
    public readonly ushort Offset;
    public static Channel<T> Of<TComponent>(FieldRef<TComponent, T> field) where TComponent : unmanaged, IComponent;
}
public delegate ref TValue FieldRef<TComponent, TValue>(ref TComponent component);

public enum TrackEndpoints : byte { Values, Displacement }

[BufferCapacity(4)]
public struct Track<T> : IBufferElement where T : unmanaged
{
    public Entity Target;                 // Entity.Null：写本实体
    public Channel<T> Channel;
    public T From, To, Current, Velocity;
    public float Start, Duration, SampleTime;
    public Curve Curve;
    public ushort Stage;
    public byte Layer;
    public TrackEndpoints Endpoints;
    public uint StartId;
    public bool Finished;
}

public struct TrackSystem<T, TOps> : ISystem<ImmediateSystemContext>
    where T : unmanaged
    where TOps : struct, IValueOps<T>;

public static class PropertyAnimation
{
    public static void Declare<T>(World world, Entity entity) where T : unmanaged;
    public static Entity To<T>(World world, Entity target, Channel<T> channel, T value, Spec spec, byte layer = 0) where T : unmanaged;
    public static Entity By<T>(World world, Entity target, Channel<T> channel, T delta, Spec spec, byte layer = 0) where T : unmanaged;
    public static void Retarget<T>(World world, Entity animation, Channel<T> channel, T value) where T : unmanaged;
    public static void Stop(World world, Entity animation);
    public static void Pause(World world, Entity animation);
    public static void Resume(World world, Entity animation);
    public static void SetSpeed(World world, Entity animation, float speed);
}
```

## Invariants
- 使用者把 `Channel<T>.Of<TComponent>(field)` 的返回值保存为静态只读字段。创建时，在默认组件实例上调用字段引用函数一次，用 `Unsafe.ByteOffset` 得到偏移；求值消费组件编号与偏移数据。
- `TrackSystem<T, TOps>` 是公开 struct 系统，与时间线系统使用相同的系统接口，使内置类型与用户类型通过相同的静态泛型路径求值。
- 系统按块遍历带轨道缓冲和启用 `Timeline` 的实体，在块内处理选中的行。当前播放、当前阶段、持续求值的轨道采样线性、缓动、弹簧、关键帧或函数指针曲线，并设置属性求值运行位 `1`。
- `SampleTime` 是弹簧上一次采样的阶段时间。弹簧步进使用本次时间与上次时间之差，暂停、游戏时钟暂停、时间缩放与速度变化均沿时间线产生效果。
- tween 完成时写目标值；弹簧按 `TOps.Distance` 与曲线容差收敛并写目标值。`Finished` 表达轨道完成，随后字段可由调用方继续修改。
- 关键帧存于动画实体的 `DynamicBuffer<Keyframe>`；调用方在初始化或配置实例时填充缓冲，`Spec.Keyframes` 选择其中的范围。
- 入口是作用于 `World` 的静态函数和普通系统，轨道数据与 ECS 查询承担查找和放置。

## Placement and controls
- `Declare<T>` 声明目标上的 `Timeline` 与 `Track<T>` 缓冲。首次创建的时间线处于禁用状态；重复声明复用已有组件。调用方可在初始化时为其它值类型、阶段与关键帧一并声明所需缓冲。
- 已声明内联存储的目标使用自身作为动画实体；普通目标使用池化动画实体。池通过 ECS 查询找到同类轨道缓冲上的空闲实体，扩容时创建实体，完成和停止时禁用时间线。
- 池化实体再次取用时，重置轨道、播放速度、时钟与阶段表，保留启动和完成计数。内联目标的时钟与速度配置由调用方持有。
- 同一目标、同一通道、同一层上的新 `To` 复用对应轨道，从字段当前呈现值出发。当前弹簧重定到弹簧时保留速度。新增其它字段的轨道沿共享时间线的当前时间启动，其它字段保留自己的进度。
- 运行中首次声明内联存储后，下一个 `To` 转移到目标自身，接替池化轨道并保留当前弹簧速度。
- `By` 表达一次衰减位移：以启动时字段值为终点，以该值加 `delta` 为起点。泛型求值系统第一次采样时通过 `TOps.Add` 形成端点，沿用 SomeUI 的单来源位移语义。
- `Retarget` 调整当前阶段中请求通道的活动轨道，沿用原曲线与时长，保留弹簧速度。同一实例有多个目标使用该通道时，全部匹配的活动轨道一起调整。开发检查要求启用的动画与匹配的活动通道。
- `Stop` 保留已呈现的字段值并禁用时间线。`Pause` 和 `Resume` 修改 `Timeline.Paused`；`SetSpeed` 修改非负、有限的 `Timeline.Speed`，暂停期间可继续设置恢复后的速度。
- 显式暂停时，属性求值保留呈现值与轨道状态并设置运行位；首个衰减位移样本也在恢复后求值。
- `Timeline.Starts` 是启动计数；`Track.StartId` 记录所属播放。禁用后再次启动时增加计数，以当前播放编号选择轨道。这使停止后启动一个值类型的字段时，其它值类型的字段保留停止时的值。
- 多目标共用一个池化时间线时，可通过公开轨道缓冲添加目标明确的轨道，并令 `StartId` 对应 `Timeline.Starts`。手工阶段轨道还需填写 `Stage`、端点、曲线与起始时间。
- 当前 #49 求值直接写字段。同一字段有不同层的活动来源时，最新的 `To` 或 `By` 接替字段，较早的来源完成。开发构建在新轨道安装后由 `DevelopmentCheck` 报告共享字段。多来源贡献与层合成的目标规则见 [[architecture/animation/Animation-Layers|Animation Layers]] 与 #50。
- 启动、重定目标、暂停、停止、调速与缓冲元素变化保留实体的表格成员关系；存储声明与池扩容负责结构变化。

## ECS field writes

`Channel<T>` stores the type number obtained from `ComponentId<TComponent>.Value`. Field updates use `World.WriteBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>())` and write the value with `Unsafe.WriteUnaligned`. Starting and retargeting also capture the field value through this byte reference. The public byte access, write versions and development range checks are defined by [[architecture/ECS/Component-Bytes|Component Bytes]]. Generic track storage uses the existing `BufferCapacity(4)` attribute defined by [[architecture/ECS/Dynamic-Buffers|Dynamic Buffers]].

## Verification
`tests/SomeEngine.Animation.Tests/PropertyAnimationTests.cs` 经公开 API 覆盖 tween 与弹簧的字段值和逐帧写版本、100 次内联启动/重定/停止的表格成员关系与实体数量、池化与多目标复用、弹簧速度连续性、位移、播放控制、自定义值运算、关键帧、函数指针、阶段轨道与共享字段检查。

稳态分配验收使用公开 `OnCreate`、`OnUpdate`、`OnDestroy` 回调组合时间推进、属性求值与收尾，覆盖内联和池化目标；预热 512 帧后连续采样 128 帧，托管分配为 0 字节。系统组调度自身的 Job 提交作用域属于通用调度路径：当前默认系统组测量为每帧 440 字节，源码入口是 `JobSubmissionTracker.Enter` 创建每个系统回调的 `Frame`。这里的零分配范围是动画系统回调。

## Relations
[[architecture/animation/Value-Ops|Value Ops]] · [[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/someui/Animation|SomeUI Animation]]
