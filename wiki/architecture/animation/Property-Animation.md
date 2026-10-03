# Property Animation

## Definition
属性动画按"组件编号 + 字节偏移"定位任意 unmanaged 组件中的字段，用轨道缓冲描述值的变化。使用者包括界面、FLIP 和游戏中的各种渐变。

```csharp
public readonly struct Channel<T> where T : unmanaged
{
    public readonly ushort Component;
    public readonly ushort Offset;
    public static Channel<T> Of<TComponent>(FieldRef<TComponent, T> field) where TComponent : unmanaged, IComponent;
}
public delegate ref TValue FieldRef<TComponent, TValue>(ref TComponent component);

[BufferCapacity(4)]
public struct Track<T> : IBufferElement where T : unmanaged
{
    public Entity Target;                 // Entity.Null：写本实体
    public Channel<T> Channel;
    public T From, To, Current, Velocity;
    public float Start, Duration;
    public Curve Curve;
    public ushort Stage;
    public byte Layer;
}

public static class PropertyAnimation
{
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
- `Channel.Of` 在一个默认实例上调用一次字段引用函数，算出偏移后存为静态只读字段。兼容 NativeAOT，不使用反射，也不用委托读写字段。
- `TrackSystem<T, TOps>` 按块遍历带 `Track<T>` 和已启用 `Timeline` 的实体，采样后写字段；写入走 ECS 的按偏移写接口，并推进写版本。
- 对同一实体、同一通道、同一层发起新的 `To` 时，在原轨道上重定目标：从当前值出发，弹簧保留速度。
- 多个来源写同一字段时，各自的贡献追加到目标的 `Contribution<T>` 缓冲，由 `ComposeSystem<T, TOps>` 按动画层规则合成，每帧只写一次。叠加层需要的基准值存于目标的 `BaseValue<T>` 缓冲，唯一写入方是调用方。
- 结构变化只发生在两处：首次声明（加 `Timeline`、`Track<T>` 等组件）和池化实体扩容。启动、停止、重定目标只修改缓冲元素和启用位。
- 不建立反向索引，不使用管理类；所有入口都是作用于 `World` 的静态函数和普通系统。

## ECS field writes

`Channel<T>` stores the type number obtained from `ComponentId<TComponent>.Value`. Field updates use `World.WriteBytes(target, channel.Component, channel.Offset, Unsafe.SizeOf<T>())` and write the value with `Unsafe.WriteUnaligned`. The public byte access, write versions and development range checks are defined by [[architecture/ECS/Component-Bytes|Component Bytes]]. Generic track storage uses [[architecture/ECS/Dynamic-Buffers|Dynamic Buffers]].

## Relations
[[architecture/animation/Value-Ops|Value Ops]] · [[architecture/animation/Animation-Layers|Animation Layers]] · [[architecture/someui/Animation|SomeUI Animation]]
