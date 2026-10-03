# Timeline

## Definition
`Timeline` 是每个动画实例的时间状态，属性动画与姿势动画共用。它是可启用组件：启动与停止只切换启用位。

```csharp
public struct Timeline : IEnableableComponent
{
    public float Time;
    public float Speed;
    public ushort Stage;
    public TimelineClock Clock;
    public uint Running;       // 本帧仍在运行的轨道类型位，由各求值系统设置
    public uint Completions;   // 完成计数，可作为依赖
}
public enum TimelineClock : byte { Game, Interface, Unscaled }
```

## Invariants
- 启动、暂停、停止、调速只修改字段或启用位，不改变 archetype。
- `TimelineSystem` 按所选时钟推进时间，并清空 `Running`；各求值系统在写入时设置自己的位；收尾阶段，没有任何位的时间线视为完成：禁用该时间线，并增加 `Completions`。
- `Interface` 时钟不受游戏暂停与时间缩放影响；`Unscaled` 只不受时间缩放影响。
- 阶段表（`DynamicBuffer<TimelineStage>`）记录开放时长的边界，边界类型有三种：`Duration`、`Settled`（所有弹簧收敛）、`External`（由 `Animation.Advance(entity)` 推进）。到达边界后进入下一阶段，后续轨道的时间相对阶段开始计算。
- 事件以计数表达（`DynamicBuffer<TimelineEvent>`：事件编号加次数）。读取方通过登记依赖或比较计数得知，不使用回调。

## Relations
[[architecture/animation/Animation-Graph|Animation Graph]] · [[architecture/animation/Property-Animation|Property Animation]] · ADR 0014
