# Animation Extension

## Definition
动画系统是库：内置的值类型、曲线、节点和求值系统只是用公开 API 写成的其中一种实现，用户的扩展与它们地位相同。

核心、属性求值、层合成与动画图在 `SomeEngine.Animation` 中实现。完整的 `Spec<T, TOps, TParameters, TState, TEvaluator>` 保留叶值、运算、参数、状态与算法类型，内置算法与消费者算法共同通过这一公开入口接入。叶值为 unmanaged，参数和状态为 struct，可以包含托管引用。

| 扩展 | 方式 |
|---|---|
| 值类型 | 实现 `IValueOps<T>`，创建完整泛型 Spec，通过 `AnimationSystems.Add` 登记具体组合 |
| 缓动 | 实现 `IEasing.Sample(float)`，与 `Tween<T, TOps, TEasing>` 组合 |
| 有状态算法 | 实现 `IMotion<T, TOps, TParameters, TState>`，通过 `Spec.Create` 与 `AnimationSystems.Add` 接入 |
| 标量函数曲线 | `Spec.FromFunction<T, TOps>(duration, function)` 适配 managed 函数指针 |
| 图节点 | `IAnimationLeaf<TParameters>` 安装具体实例数据；组合节点调用公开构建原语 |
| 运行时行为（物理驱动、沿路径运动等） | 写自己的组件加系统，读同一个 `Timeline`，并设置自己的 `Running` 位 |
| 默认配置 | `AnimationSystems.AddDefaults(schedule)` 只是把内置系统加入调度的便利函数，可以不用 |

## Algorithm lifecycle

`Spec.Parameters` 是算法参数，`Spec.Boundary` 从 `TEvaluator.Describe` 得到阶段结束方式与时长。同一完整类型组合的不同参数值共享代码。定义持有共享路径与关键帧引用，实例的类型化 ECS 数据持有算法历史。

| 数据 | 含义 |
| --- | --- |
| `MotionBoundary(End, Duration)` | Duration、Settled 或 External 阶段描述 |
| `MotionValues<T>(From, To, Current)` | 本来源起点、目标与上一次输出 |
| `MotionTime(Time, Delta)` | 本阶段采样时间与距上次采样的增量 |
| `MotionSample<T>(Value, Finished)` | 当前输出与算法完成结果 |

`IMotion` 提供 `Describe`、`Initialize`、`Evaluate`、`Retarget`，以及带共同默认实现的 `UpdateParameters`、`ParametersEqual`、`Release` 和 `EvaluateBatch`。初始化与回收分别调用 `Initialize`、`Release`；参数变化调用 `UpdateParameters`；目标变化以本来源当前输出重新确定端点并调用 `Retarget`。参数与目标相等时保留时间与状态。

内置 `Tween` 使用 `TweenParameters` 与 `TweenState`；`Spring` 使用 `SpringParameters` 与 `SpringState<T>`，重定目标延续 `Velocity`；`Keyframes` 使用持有只读关键帧引用的 `KeyframeParameters`，实例以 `KeyframeState.Segment` 缓存当前区间。消费者在自己的状态中延续自己的历史。

## Typed batches

`MotionBatch<T, TParameters, TState>` 是本次执行范围内的 ref struct，直接描述实际 `Span<MotionData<T, TParameters, TState>>`，或连续的参数、状态、值、时间与输出切片。参数与时间切片长度为 1 时表示共享输入，每个元素也可以使用自己的时间。

公开访问包括 `Count`、`IsActive(index)`、返回只读引用的 `Parameters(index)`、返回引用的 `State(index)`、`Values(index)`、`Time(index)` 和 `SetSample(index, sample)`。`Data` 与对应 Span 属性供专用批量实现直接访问实际存储。`MotionData` 默认内联容量为 1，声明入口按实际组合预留容量。

`IMotion.EvaluateBatch<TMotion>` 的默认循环对具体算法执行逐值 `Evaluate`。算法可以实现包含整段循环的批量版本。两者通过相同的 `MotionDispatcher<T, TOps, TParameters, TState, TEvaluator>.EvaluateBatch(ref batch)` 进入；共享的 `Function` 地址为 `delegate* managed`，每个实际批次调用一次。`FunctionTween` 的标量函数地址按活动样本调用，其成本单独计入。

```csharp
var hover = Spec.Tween<float, FloatOps, CubicOut>(0.15f);
var spring = Spec.Spring<float, FloatOps>(180, 20);
var path = Spec.Create<float, FloatOps, PathParameters, PathState, PathMotion>(parameters);
AnimationSystems.AddDefaults(schedule);
AnimationSystems.Add(schedule, hover);
AnimationSystems.Add(schedule, path);
```

用户系统也可直接放在公开 `TimelineSystem` 与 `TimelineEndSystem` 之间，通过 `Timeline.Submit(sample)` 提交共同运行结果。共同调度按 Timeline、图、层权重求值与合成、字段求值与合成、图与 Timeline 收尾组织。

## Property evaluators
用户定义值类型与其 `ops struct` 后，通过 `Channel<T>.Of<TComponent>` 声明字段，把完整泛型 Spec 传给 `PropertyAnimation.Declare`、`To` 和 `By`，并通过 `AnimationSystems.Add(schedule, spec)` 登记具体批次、图输出与合成。`Retarget` 延续本来源状态；求值与位移端点计算都调用同一个 `TOps`。既有 `TrackSystem<T,TOps>` 同时支持便利输入与手动系统编排。

`AnimationSystems.AddDefaults(SystemGroup<ImmediateSystemContext> schedule, Entity time = default)` 通过 `Add` 登记 float、Vector2/3/4、Quaternion、Color、Thickness、Rect、Matrix3x2 的 Linear Tween、Spring、Keyframes、FunctionTween 组合，并在绑定时解析已有 Curve 输入。其他具体缓动与用户组合通过 `Add(schedule, spec)` 登记。

共同动画管线在首次创建时按阶段展开到一个 ECS 系统组：`TimelineSystem(time)`、`GraphSystem`、float 层权重求值与合成、字段求值与合成、`GraphEndSystem`、`TimelineEndSystem`。具体系统各自保留版本基线与查询。首次更新前追加的组合进入相应阶段，重复组合共用一个入口。调用方也可按自己的值类型与行为直接组合公开系统：

用户系统可以通过 `AnimationSystems.Add(schedule, system, AnimationPhase.Properties)` 加入共同字段求值阶段；`Timeline`、`Graph`、`LayerWeights`、`LayerComposition`、`CompositionInputs`、`Composition`、`Completion` 也提供明确接入位置。`CompositionInputs` 位于属性最终合成之后、具体 Composition 系统之前，用于将当前参数与时钟形成组合输入；GPU 姿势系统在该阶段读取当前 `GraphParameters`，后续 CPU 依赖节点与同步骨骼消费同次输入。阶段展开时采用低频系统装配，热更新沿 ECS 的 managed 函数指针系统入口执行。

```csharp
var schedule = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
schedule.Add(new TimelineSystem(time));
schedule.Add(new GraphSystem());
schedule.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
schedule.Add(new TrackSystem<MyValue, MyOps>());
schedule.Add(new ComposeSystem<MyValue, MyOps>());
schedule.Add(new GraphEndSystem());
schedule.Add(new TimelineEndSystem());
```

外部字段求值系统在目标上声明 `Contribution<T>` 缓冲，经公开缓冲入口追加 `(Channel, Layer, Value, Source, Output)`，并放在相应合成阶段之前。层权重与字段贡献的阶段、来源排序和基准值规则由 [[architecture/animation/Animation-Layers|Animation Layers]] 定义。

`tests/SomeEngine.Animation.Tests/PropertyAnimationTests.cs` 在测试工程中声明 `Meter` 和 `MeterOps`，经相同公开入口验证 tween、重定目标、衰减位移与弹簧。`AnimationLayersTests.cs` 继续验证自定义值类型的多来源合成与外部求值系统的贡献入口。

## Verification
`MotionTests` 从消费者程序集验证缓动、值运算、包含托管引用的参数与状态、生命周期、Spring 速度、共享关键帧、逐元素时间和专用批量。`tools/SomeEngine.Animation.Checks` 复用同一消费者源码，提供 Release / NativeAOT 行为与 `--benchmark` 规模成本入口。

2026-10-16 的 Release 定向检查通过 27 项，覆盖 Motion、Timeline、ValueOps 与完整类型化图公开行为；行为规则检查为 0 项违规。队列 `01c002753d7a438b89c8ff24354c7e7e` 退出码 0，原始日志与结果保存在 [[evidence/Animation/2026-10-16/Typed-Execution|类型化执行证据]]。

```powershell
& 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1' dotnet test tests/SomeEngine.Animation.Tests/SomeEngine.Animation.Tests.csproj -c Release '--filter=FullyQualifiedName~TypedGraphTests|FullyQualifiedName~MotionTests|FullyQualifiedName~TimelineTests|FullyQualifiedName~ValueOpsTests' --disable-build-servers '-m:1' '-nodeReuse:false' '--property:UseSharedCompilation=false' '--property:PublishAot=false'
```

## Relations

[[architecture/animation/Value-Ops|Value Ops]] · [[architecture/animation/Timeline|Timeline]]
