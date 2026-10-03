# Animation Extension

## Definition
动画系统是库：内置的值类型、曲线、节点和求值系统只是用公开 API 写成的其中一种实现，用户的扩展与它们地位相同。

当前核心、属性求值与层合成在 `SomeEngine.Animation` 中实现：时间线、轨道与合成使用公开 struct 系统，值运算通过 `IValueOps<T>` 和 `ValueOps<T, TOps>` 扩展，曲线接收数据与函数指针。图的目标接缝见对应页面。

| 扩展 | 方式 |
|---|---|
| 值类型 | 写一个实现 `IValueOps<T>` 的 struct，把 `TrackSystem<T, TOps>` 加入调度（需要合成时再加 `ComposeSystem<T, TOps>`） |
| 曲线 | 写 `Curve` 数据，或提供函数指针 |
| 图节点 | 写一个调用构建器原语的普通函数 |
| 运行时行为（物理驱动、沿路径运动等） | 写自己的组件加系统，读同一个 `Timeline`，并设置自己的 `Running` 位 |
| 默认配置 | `AnimationSystems.AddDefaults(schedule)` 只是把内置系统加入调度的便利函数，可以不用 |

## Invariants
- 没有注册表、管理类或内部入口。
- 用户系统可以使用 `SystemGroup<ImmediateSystemContext>`，把自己的求值系统放在 `TimelineSystem` 与 `TimelineEndSystem` 之间，并为当前阶段仍在运行的工作设置 `Timeline.Running` 位。

## Property evaluators
用户定义值类型与其 `ops struct` 后，通过 `Channel<T>.Of<TComponent>` 声明字段，使用 `PropertyAnimation.To`、`By` 和 `Retarget`，并把 `TrackSystem<T, TOps>` 放在时间推进与收尾之间。求值与位移端点计算都调用同一个 `TOps`。

`AnimationSystems.AddDefaults(SystemGroup<ImmediateSystemContext> schedule, Entity time = default)` 按顺序加入 `TimelineSystem(time)`、float 层权重的求值与合成、float、Vector2/3/4、Quaternion、Color、Thickness、Rect、Matrix3x2 的全部字段求值系统、对应的全部字段合成系统，以及 `TimelineEndSystem`。调用方也可按自己的值类型与行为直接组合这些公开系统。

```csharp
var schedule = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
schedule.Add(new TimelineSystem(time));
schedule.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new TrackSystem<MyValue, MyOps>());
schedule.Add(new ComposeSystem<MyValue, MyOps>());
schedule.Add(new TimelineEndSystem());
```

外部字段求值系统在目标上声明 `Contribution<T>` 缓冲，经公开缓冲入口追加 `(Channel, Layer, Value, Source, Output)`，并放在相应合成阶段之前。层权重与字段贡献的阶段、来源排序和基准值规则由 [[architecture/animation/Animation-Layers|Animation Layers]] 定义。

`tests/SomeEngine.Animation.Tests/PropertyAnimationTests.cs` 在测试工程中声明 `Meter` 和 `MeterOps`，经相同公开入口验证 tween、重定目标、衰减位移与弹簧。`AnimationLayersTests.cs` 继续验证自定义值类型的多来源合成与外部求值系统的贡献入口。

## Relations
[[architecture/animation/Value-Ops|Value Ops]] · [[architecture/animation/Timeline|Timeline]]
