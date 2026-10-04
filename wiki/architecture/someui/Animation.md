# SomeUI Animation

## Definition
SomeUI 是引擎动画库中属性动画的使用者（[[architecture/Animation|Animation]]，ADR 0014），只增加两项与界面相关的衔接：`.Animate` 和 FLIP。界面动画使用 `Interface` 时钟，游戏暂停时照常运行。

## Invariants
- **`.Animate(channel, spec)`：** 首次调用时，在节点上加入禁用状态的 `Timeline` 和对应的 `Track<T>`。此后调用方对该字段的写入（参数或修饰）成为基准值，并在内联轨道上重定目标；呈现值由属性动画写回字段。整个过程没有结构变化，也不创建新实体。基准值的唯一写入方是调用方，呈现值的唯一写入方是动画系统。
- **FLIP：** `Flip.Enable(entity, spec)` 首次在节点上加入 `PresentationOffset`（矩阵与不透明度）及其内联轨道。`FlipSystem` 在布局之后比较前后两帧同一实体的快照（边界、变换、不透明度），算出反向的 `PresentationOffset`，再以内联轨道把它动画回到单位值。FLIP 只写 `PresentationOffset`，不写用户变换；渲染时叠加布局结果、用户变换和 `PresentationOffset`。只按同一实体匹配，不做启发式的跨实体匹配。
- 一次性流程可以直接在异步构建中使用 `await NextFrame()`，或者等待时间线完成计数。

## Public API

`AnimateModifiers` 位于 `SomeEngine.Views`；`Flip`、`FlipSystem`、`PresentationOffset`、
`PresentationChannels` 与 `ElementTransform` 位于 `SomeEngine.Elements`。

```csharp
public static class AnimateModifiers
{
    public static Entity Animate<T>(this Entity node, Channel<T> channel, Spec spec) where T : unmanaged;
}
public static class Flip
{
    public static void Enable(Entity node, Spec spec);
    public static void Enable(World world, Entity node, Spec spec);
}
```

构建与计算中的声明式入口使用 `Scope.World`。显式 World 重载供应用直接声明 FLIP。
`SurfaceChannels.Fill` 是保存为静态只读值的普通 `Channel<Color>`；用户字段通过
`Channel<T>.Of<TComponent>` 声明，并使用自己的 `TrackSystem<T, TOps>` 求值。

```csharp
Column().Background(hovered ? hoverColor : normalColor)
    .Animate(SurfaceChannels.Fill, Spec.Tween(0.15f));
Flip.Enable(row, Spec.Tween(0.2f));
```

## Declarative writes

首次声明建立禁用的内联时间线、轨道缓冲、调用方基准值与字段声明缓冲。
初次写入作为初值直接呈现。后续 `Scope.Write` 副本从调用方的基准值开始，
保留同组件其它字段的普通写入。视图参数更新也进入这条字段提交路径。
组件提交保留动画字段当前呈现值；最外层构建成功后，提交目标值到
`PropertyAnimation.SetBase`，目标发生变化时调用 `PropertyAnimation.To`。
父构建失败时撤回本次目标，已有过渡继续使用此前成功提交的目标。

相同字段字节值保留已有目标及其进度；重跑可以更新以后采用的 Spec。
新目标沿属性动画规则从当前呈现值继续，弹簧沿其公开 API 保留速度。
只改变动画目标的参数更新保留呈现字段的写版本；实际采样沿属性动画写版本规则推进。
提交接缝使用声明时绑定的类型专门化函数地址，字段位置仍由组件编号与偏移表达；
逐帧求值由普通静态泛型系统完成。基准值由属性动画的 `BaseValue<T>` 持有。

## FLIP frame data

首次 `Enable` 声明单位 `PresentationOffset`、矩阵和透明度的内联轨道、调用方的
`FlipSettings` 与系统持有的 `FlipFrame`。重复声明更新 Spec 并保留前帧数据。
应用在动画和布局之后注册 `FlipSystem`，顺序见 [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。

`FlipSystem` 保存同一实体上一帧的布局矩形、用户矩阵与透明度。
首次出现的实体建立前帧数据；缺少布局结果的帧清除其匹配状态。
布局或用户变换变化时，反向矩阵把新矩形映射到当前呈现，包含正在衰减的偏移，
再通过公开 `PropertyAnimation.To` 回到单位矩阵。透明度变化重定它自己的轨道，
矩阵变化重定矩阵轨道，彼此保留独立进度。布局零尺寸方向使用单位尺寸倍率，
奇异的用户矩阵使用位置差；目标透明度为零时使用单位倍率。
呈现坐标与透明度的提取规则由 [[architecture/someui/Render-Path|Render Path]] 维护。

## Evidence

`tests/SomeEngine.Elements.Consumer.Tests/InterfaceAnimationTests.cs` 经公开 API 验证悬停颜色、
中途反向、参数与修饰的相同目标重跑、实体表格成员关系、构建失败后的目标恢复、
列表重排、用户变换、上一帧实体匹配及游戏暂停。
尺寸、局部矩阵与透明度同时变化时，两个矩形角点与透明度保持呈现连续，并回到新布局的单位偏移。
`InterfaceAnimationChecks.cs` 将用户自定义值运算、内联颜色轨道与中断 FLIP 的相同
行为检查复用于 `SomeEngine.Views.AotSmoke`。
渲染提取的呈现行为见 `PresentationExtractionTests.cs`。

2026-10-04 的验收代码提交为 `8f35d86b8d6a2faead6f8469b1fb802d5ccd9d91`，
包含集成分支 `f72a355041cdecced3cb0778e50ed7fa08c987b3`。以
`dotnet test tests/<项目>/<项目>.csproj --nologo -v quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`
运行 Views、Animation、Layout、Elements.Consumer 与 Elements.Render 的完整 Debug 测试，
分别通过 259、125、94、57、11 项，共 546 项。
后两组增加 `-c Release` 分别通过 41 与 11 项。
`dotnet publish tools/SomeEngine.Views.AotSmoke/SomeEngine.Views.AotSmoke.csproj -c Release -r win-x64`
发布的原生程序通过共享行为检查；`Test-Checkpoint.ps1 -Executable <原生程序>`
通过跨进程保存、类型编号重映射、下一帧重建与依赖验收。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Render-Path|Render Path]]
