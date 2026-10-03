# Timeline

## Definition
`Timeline` 是每个动画实例的时间状态，属性动画与姿势动画共用。它是可启用组件：启动与停止只切换启用位。

```csharp
public struct Timeline : IEnableableComponent
{
    public Timeline() => Speed = 1;
    public float Time;
    public float Speed;
    public ushort Stage;
    public TimelineClock Clock;
    public uint Running;       // 本帧仍在运行的轨道类型位，由各求值系统设置
    public uint Completions;   // 完成计数，可作为依赖
}
public enum TimelineClock : byte { Game, Interface, Unscaled }

public enum StageEnd : byte { Duration, Settled, External }
[BufferCapacity(4)]
public struct TimelineStage : IBufferElement { public StageEnd End; public float Duration; }
[BufferCapacity(4)]
public struct TimelineEvent : IBufferElement { public ushort Id; public uint Count; }
```

`SomeEngine.Animation` 的帧接缝使用 `SystemGroup<ImmediateSystemContext>`：先加入 `TimelineSystem`，再加入各求值系统，最后加入 `TimelineEndSystem`。三个系统阶段通过同一个实体上的 `Timeline` 交换时间和运行位。

```csharp
Entity time = world.CreateEntity(new AnimationTime(delta, gameScale: 1, gamePaused: false));
systems.Add(new TimelineSystem(time));
// 此处加入属性、姿势或用户自己的求值系统。
systems.Add(new TimelineEndSystem());
```

调用方在帧开始前替换 `AnimationTime`，它是原始帧时长、游戏时间缩放与暂停标志的主要持有者。`TimelineSystem()` 直接使用 `ImmediateSystemContext.DeltaTime`，适合三种时钟都采用同一时长的调用者。`new Timeline()` 的速度为 1，原始零值数据的速度为 0。

## Invariants
- 启动、暂停、停止、调速只修改字段或启用位，不改变 archetype。
- `TimelineSystem` 按所选时钟推进时间，并清空 `Running`；各求值系统为本阶段仍在运行的工作设置自己的位；`TimelineEndSystem` 处理边界并完成已经结束的时间线：禁用该时间线，并增加 `Completions`。暂停时仍存续的求值工作同样设置运行位。
- `Interface` 时钟不受游戏暂停与时间缩放影响；`Unscaled` 只不受时间缩放影响。
- 阶段表（`DynamicBuffer<TimelineStage>`）记录边界，边界类型有三种：`Duration`、`Settled`（本阶段的运行位全部清空）、`External`（由 `Animation.Advance(world, entity)` 推进）。`Time` 相对当前阶段；固定时长边界在收尾时减去已经经过的时长，并把越界时间带入后续阶段；收敛与外部边界进入下一阶段时把时间归零。新的收敛阶段交给下一帧的求值系统运行。
- 固定时长等待和外部等待都使时间线保持启用；阶段表耗尽且运行位清空后完成。使用阶段表时，其数量最多为 `ushort.MaxValue`。直接使用运行位的动画可以省去阶段缓冲。
- 事件以计数表达（`DynamicBuffer<TimelineEvent>`：事件编号加次数）。调用方先声明事件缓冲，求值系统通过 `Animation.EmitEvent(world, entity, id)` 推进对应计数。读取方通过登记依赖或比较计数得知事件。
- 阶段和事件缓冲使用 ECS 的内联缓冲机制；启用、完成、外部推进和已有事件计数的写入都保留实体的表格成员关系。

## Verification
`tests/SomeEngine.Animation.Tests/TimelineTests.cs` 通过公开 World、系统组和组件/缓冲 API 验证时钟暂停、时间缩放、运行位、完成计数、三个阶段边界与事件。`ObserveEntityTables()` 的结果验证反复启用和完成后的表格成员关系。开发构建通过 `DevelopmentCheck` 报告边界类型和外部推进的使用错误。

## Relations
[[architecture/animation/Animation-Graph|Animation Graph]] · [[architecture/animation/Property-Animation|Property Animation]] · ADR 0014
