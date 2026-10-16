# SomeUI Animation

## Definition
SomeUI 是引擎动画库中属性动画的使用者（[[architecture/Animation|Animation]]，ADR 0014），只增加两项与界面相关的衔接：`.Animate` 和 FLIP。界面动画使用 `Interface` 时钟，游戏暂停时照常运行。

## Invariants
- **`.Animate(channel, spec)`：** 首次声明节点的时间线、通道、具体算法组合及类型化容量。后续参数、修饰与 `Scope.Set` 提交调用方目标；内联来源保存自身输出，最终呈现值由属性动画写回。声明的类型集合及容量覆盖启动、重定目标、切换和停止。基准值由调用方提交，算法历史由实例持有。
- **引用路径：** `Scope.Set(node, channel, value)` 与生成的字段目标方法保存独立叶值目标，在构建提交时启动或更新动画。真实引用对象在呈现写回时得到当前输出；根组件版本与 `State<T>.Value` 自身的版本分别沿公开写入行为推进。
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

完整强类型 `Spec<T, TOps, TParameters, TState, TEvaluator>` 由 `.Animate` 原样传入声明和求值。
应用在首次更新前通过 `AnimationSystems.Add(schedule, spec)` 登记具体组合；
`GameFrameSystems.AddAnimation(spec)` 把用户算法加入相同的默认界面阶段。
普通字段、嵌套值、引用字段及可写属性都使用生成的统一 Channel。

```csharp
[GenerateFields]
[ComponentField("Progress.Value", Name = "ProgressValue")]
public struct PanelData : IComponent
{
    public float Opacity;
    public State<float> Progress;
}

var radius = Spec.Tween<float, FloatOps, CubicOut>(0.15f);
node.Animate(PanelDataChannels.ProgressValue, radius);
Scope.Set(node, PanelDataChannels.ProgressValue, hovered ? 16f : 8f);
// 生成的目标修饰器采用相同提交入口。
node.SetProgressValue(hovered ? 16f : 8f);
```

`Scope.Set` 也提供节点切片与对应值切片的批量重载。字段身份、具体路径及访问函数由共同生成器维护；
普通字段与值嵌套的组件副本继续提交参数目标，跨引用路径采用独立叶值副本。

```csharp
Column().Background(hovered ? hoverColor : normalColor)
    .Animate(SurfaceChannels.Fill, Spec.Tween(0.15f));
Flip.Enable(row, Spec.Tween(0.2f));
```

## Declarative writes

首次声明建立禁用的内联时间线、轨道缓冲、调用方基准值与字段声明缓冲。
初次写入作为初值直接呈现。后续 `Scope.Write` 副本从调用方的基准值开始，
保留同组件其它字段的普通写入。视图参数更新也进入这条字段提交路径。
组件提交保留动画字段当前呈现值。同步构建在最外层成功完成时提交目标；
异步构建在初次挂起、恢复后再次挂起以及成功完成时提交该段目标。
目标写入 `PropertyAnimation.SetBase`，目标变化时调用 `PropertyAnimation.To`。
整次构建失败时按记录的逆序恢复此前完整构建的目标，从当帧呈现值继续过渡。
普通组件与参数的恢复同样保留动画字段当帧呈现值。

动画写入沿 `ChildChanges` 的既有提交与恢复路径持有。同步父视图中的异步子视图
首次挂起时，动画记录随其它恢复数据保留到子视图；开始位置由 `FirstAnimation`
随 `BeginChildren` 和 `ReplaceChanges` 保存、调整。每份记录保存一次提交前目标，
首次提交后继续承担恢复；父子共享的记录按使用它的构建数保留，最后一个构建结束时释放恢复值。
作用域复用时，`ChildChanges.Clear` 同时清空动画记录。

`Launch` 的异步计算沿同一提交与恢复路径运行。每段提交后归还计算的写入副本，
动画恢复记录继续保留该段目标；续跑写入取得新副本，完成或取消时按原构建数据释放。
普通字段恢复统一经过 `ChildChanges.RestoreValue`，先保留动画呈现值，再比较整值，
实际变化通过 ECS 替换推进写版本，已读取该字段的视图和计算沿现有依赖路径更新。

强类型路径通过 `TOps.Equals` 比较叶目标，通过 `TEvaluator.ParametersEqual` 比较参数；相同提交保留已有时间和状态。
参数变化调用 `UpdateParameters`，目标变化调用 `Retarget`，已声明算法切换执行释放和初始化生命周期。
新目标沿属性动画规则从当前呈现值继续，弹簧沿其公开 API 保留速度。
只改变动画目标的参数更新保留呈现字段的写版本；实际采样沿属性动画写版本规则推进。
提交接缝使用声明时绑定的具体泛型函数，字段身份由组件编号与生成字段编号表达；
共享描述保存实际 CLR 偏移或类型化路径。逐帧求值由保留完整算法类型的批次完成。
基准值由属性动画的 `BaseValue<T>` 持有，绑定参数由具体 `FieldMotion` 数据持有，运行历史由动画实例持有。

独立叶值副本沿 `ChildChanges` 的动画记录参加同一段提交、挂起、续跑、恢复与取消。
每段提交保存此前叶目标，恢复时从当前呈现继续转向此前目标；普通参数副本保持原有提交顺序。

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
`AsyncAnimationTests.cs` 验证挂起与恢复段的目标、相同目标续跑、跨帧失败、
参数呈现连续性，以及同步父与异步子共享计算记录的恢复。
`AsyncAnimationLaunchTests` 验证 Launch 每段目标提交、失败恢复，以及普通构建、嵌套子构建
和 Launch 取消后的连续重定目标与旧任务清理。
原生入口的 `AsyncAnimationChecks` 使用同一公开接缝覆盖嵌套计算与动画目标恢复。

2026-10-04 的验收代码提交为 `8f35d86b8d6a2faead6f8469b1fb802d5ccd9d91`，
包含集成分支 `f72a355041cdecced3cb0778e50ed7fa08c987b3`。以
`dotnet test tests/<项目>/<项目>.csproj --nologo -v quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`
运行 Views、Animation、Layout、Elements.Consumer 与 Elements.Render 的完整 Debug 测试，
分别通过 259、125、94、57、11 项，共 546 项。
后两组增加 `-c Release` 分别通过 41 与 11 项。
`dotnet publish tools/SomeEngine.Views.AotSmoke/SomeEngine.Views.AotSmoke.csproj -c Release -r win-x64`
发布的原生程序通过共享行为检查；`Test-Checkpoint.ps1 -Executable <原生程序>`
通过跨进程保存、类型编号重映射、下一帧重建与依赖验收。

随后以代码提交 `4b73b87458e58d7db390bfb8b756db074ee828cc` 合入集成分支的
`3da0051c0116fb962997fa40d3f3a0c12803c257`，再次执行受影响的 Elements.Consumer
与 Elements.Render 完整测试：Debug 分别通过 64 与 11 项，Release 分别通过 48 与 11 项。
更新后的原生发布程序及跨进程 checkpoint 验收同时通过。

异步生命周期适配的验收代码为 `a72d918c9970d6e7ca23292a46c829658858d677`，
包含 `codex/someui` 的 `5d3be07dc7e38c61783d85a5ad5d807d2c10f98b`。
`AsyncAnimationTests` 经公开 API 通过 6 个挂起、恢复与失败场景，原有界面动画 10 项同时通过。
Views 的完整 Debug／Release 分别通过 290／260 项，Elements.Consumer 分别通过 161／122 项，
Solid 分别通过 13／12 项。测试命令沿上面的单进程配置，追加
`-- xUnit.MaxParallelThreads=1 xUnit.ParallelizeTestCollections=false` 固定测试集合执行条件。
动画、布局与提取的回归分别通过 125、94、11 项，提取 Release 通过 11 项。
原生发布中的 `AsyncAnimationChecks`、完整行为入口和 `Test-Checkpoint.ps1` 同时通过；
临时发布目录在运行后清理。

画布与异步计算适配的验收代码为 `c91f61a5cbaf72f6de079dd4f5f15eab86710bcf`，
合入 `codex/someui` 的 `8c953fae65deb55d925043a1377ec48253744dc1`，其中包含指定的
`55cfda50379cedd4a0726bc627d260a8f3d9ee7f` 及既有评审修复、Launch/Resource 和三材质画布实现。
公开异步动画用例增至 10 项，与原有界面动画 10 项共同验收；新增画布呈现用例通过
D3D12 WARP/Vulkan、屏幕/世界画布四种组合的像素读回。

| 测试范围 | Debug | Release |
| --- | ---: | ---: |
| Views 完整测试 | 305 | 273 |
| Elements.Consumer 完整测试 | 253 | 187 |
| Solid 完整测试 | 50 | 46 |
| Elements.Render 完整测试 | 34 | 32 |
| RenderGraph 画布、字形、图片与世界画布绘制 | 31 | 31 |
| Layout 完整测试 | 95 | 75 |
| Animation 完整测试 | 125 | 117 |
| 应用帧顺序与输入完整测试 | 30 | 30 |
| 合计 | 923 | 791 |

各测试项目使用 `dotnet test tests/<项目>/<项目>.csproj -c <配置> --nologo -v quiet -m:1
-nodeReuse:false -p:UseSharedCompilation=false -- xUnit.MaxParallelThreads=1
xUnit.ParallelizeTestCollections=false`。RenderGraph 在相同命令中追加
`--filter 'FullyQualifiedName~Canvas|FullyQualifiedName~GlyphDrawingTests|FullyQualifiedName~ImageDrawingTests'`，
范围包含静止帧、资源更新、取消帧、裁剪、透明叠加、世界投影与深度测试。
应用项目的 MeshOptimizer 依赖按仓库子模块版本 `8d15f4e182f77df0c9af3aaf7ccd5dd8aeafc2a4`
初始化后执行。

原生发布使用 `dotnet publish tools/SomeEngine.Views.AotSmoke/SomeEngine.Views.AotSmoke.csproj
-c Release -r win-x64 -o artifacts/someui-ticket-52-aot --nologo -v quiet -m:1
-nodeReuse:false -p:UseSharedCompilation=false`。
发布后的 `SomeEngine.Views.AotSmoke.exe` 实际运行通过，包括 Launch 的跨段目标提交、
失败恢复及取消后旧任务清理；`Test-Checkpoint.ps1 -Executable <原生程序>` 通过跨进程保存、
类型编号重映射、下一帧重建与依赖验收。原生发布目录与发布生成的 RID 锁文件变化在运行后清理。

## Relations
[[architecture/animation/Property-Animation|Property Animation]] · [[architecture/someui/Data-Writers|Data Writers]] · [[architecture/someui/Render-Path|Render Path]]
