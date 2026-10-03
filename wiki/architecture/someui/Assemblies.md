# Assemblies

## Current design
| 程序集 | 内容 | 依赖 |
|---|---|---|
| `SomeEngine.Views` | `IView`、`Scope`、`BuildTask`、`VersionCell`、`State<T>`、`ViewRebuildSystem`、`ElementTree` 层级域；共享布局协议、约束及节点数据 | ECS、ECS.Serialization、ECS.Systems、Job、Core（开发检查与几何） |
| `SomeEngine.Views.Generators` | 同名函数生成、`[Composable]` 拦截、`[State]`、`[Component]`、分析器、CS1998 屏蔽、视图类型序列化注册 | — |
| `SomeEngine.Layout` | `LayoutSystem`、`Absolute`、布局修饰；`Flex`、`Grid`、`Stack` 与缓存由后续票扩展 | Views |
| `SomeEngine.Animation` | 引擎级动画核心与属性动画（见 [[architecture/Animation|Animation]]）；FLIP 与 `.Animate` 在 Elements 中 | ECS、Job |
| `SomeEngine.Typography` | 字形排版、字体、图集 | — |
| `SomeEngine.Elements` | `Text`、`Image` 等元素、`Surface` 修饰，行为部件，内置控件与复合部件，输入与焦点系统，`.Animate` 衔接 | Views、Layout、Typography、Animation |
| `SomeEngine.Elements.Render` | 提取系统、渲染功能、内置材质 | Elements、Render |
| `SomeEngine.Views.Compose` / `.SwiftUI` / `.Solid` | 各前端的协调原语 | Views |

## Invariants
- `SomeEngine.Elements` 及之后的程序集只使用底层公开 API，不使用 `InternalsVisibleTo` 或任何内部入口。
- 只面向 NativeAOT：不使用反射、`MakeGenericType`、`Activator`、表达式树编译。

## 共享布局协议（#24）

`ILayout`、`LayoutPass`、`LayoutChildren`、`Constraints`、`Size`、`Box`、
`LayoutRect`、`Canvas` 与 `AbsolutePosition` 位于 Views 的基础层，命名空间为
`SomeEngine.Layout`。Views 的单一类型表登记构建和测量函数指针；
Layout 通过基础层内部入口驱动每个画布，共用当次尺寸与位置数据。
这一拆分使协议接入已有视图注册，依赖保持为 Layout → Views。
具体接口与默认值由 [[architecture/someui/Layout|Layout]] 维护。

## 源生成接入（#19）

`SomeEngine.Views.Generators` 使用 Roslyn 增量生成器、`ViewAnalyzer`
和 `BuildWarningSuppressor` 提供同名函数、调用位置身份、编译期接入检查与 CS1998 屏蔽。
`SomeEngine.Views` 的 NuGet 包将生成器放入 `analyzers/dotnet/cs`，
并通过 `buildTransitive/SomeEngine.Views.props` 启用 `SomeEngine.Views.Generated` 拦截命名空间。
仓库内由 `Directory.Build.props` 与 `Directory.Build.targets` 接入引用 Views 的项目。
视图函数的参数与可见范围见 [[architecture/someui/Views|Views]]；
编译诊断见 [[architecture/someui/Checks|Checks]]。

`SomeEngine.Views.Compose`（#58）提供 `ComposableAttribute`，依赖 Views。
直接引用 Compose 包时，生成器 DLL 与 buildTransitive props 随 Views 依赖接入，
由 Views 包持有同一份生成器与拦截命名空间配置。
仓库中直接引用 Compose 的项目同样由 `Directory.Build.targets` 接入生成器。
Compose 的视图生成、调用位置拦截与跨程序集类型复用由同一个 `ViewGenerator` 提供，
声明与调用诊断由 `ComposeAnalyzer` 提供，接口形状见
[[architecture/someui/Compose-Frontend|Compose Frontend]]。

## Elements 接入（#28）

`SomeEngine.Elements` 提供 `Surface` 和 `SurfaceModifiers`，当前引用 Views 与 Layout。
Surface 的字段、默认值和调用方写入规则见 [[architecture/someui/Modifiers|Modifiers]]。
后续元素按实际使用的公开能力接入 Typography 与 Animation。

`SomeEngine.Elements.Consumer.Tests` 是外部重写工程，直接引用 Views、Layout 与 Elements
的公开项目，用户示例位于 `Rewrites/`。每个内置控件的等价重写在这一工程中接入同一组
公开行为用例；首个示例覆盖 Surface 的三种修饰。
该工程还引用 Animation、Typography、Compose 与 SwiftUI 的公开项目，
把现有 SomeUI 各层的程序集属性纳入架构验收。

`AssemblyBoundaryTests.ElementLayerUsesPublicAssemblyAccess` 读取这个消费者所引用的
引擎程序集、现有 SomeUI 各层及其传递依赖，并检查编译结果中的 `InternalsVisibleTo` 声明。
受检查的公开层包括 Elements 系列（含 Elements.Render）、Compose、SwiftUI、Solid，
以及引用这些层的消费者。带公钥的友元名称按程序集名称识别。
发现向这些公开层授予内部访问的声明时，架构验收失败并列出声明来源与目标程序集。

## 视图序列化（#66）

界面节点参与 ECS 检查点和存档。应用先调用 `ViewSerialization.Register(registry)`，
再调用每种视图对应的 `CardFunctions.RegisterSerialization(registry)`。
每个视图类型均有注册入口；带主构造函数的视图还提供同名声明函数。
`GroupFunctions.RegisterSerialization(registry)` 注册引擎提供的透明容器。
其它应用组件沿用 ECS 注册入口。

生成器为参数的 8–64 位整数、`float`、`double`、布尔、字符、枚举、`Guid`、`Entity` 和 `string`
生成字段 codec，包含普通 struct 的私有字段和 record struct 的只读参数。
字段按名称排序，类型身份由程序集名称与视图完整名称生成，字段名称、类型、枚举定义和存储方式形成 schema。
字段读取通过编译期生成的 `UnsafeAccessor` 完成；`Entity` 参数同时生成引用重映射代码。
带其它字段形状的视图通过 `CardFunctions.RegisterSerialization<CardCodec>(registry)`
接入应用的 `IComponentCodec<Card>`。
普通视图通过 `CardFunctions.SerializationType` 提供生成的类型数据；
应用 codec 可向注册方法传入自定义 schema，保留该类型 GUID 并随存档编码版本更新 schema。
泛型视图的每个闭合类型由应用提供独立的 `SerializationTypeKey`：
`CardFunctions.RegisterSerialization<int, CardCodec>(registry, typeKey)`。
闭合类型的字段满足生成条件时，也提供 `RegisterSerialization<T>(registry, typeKey)`。
应用为同一种视图类型在各注册表中使用同一个类型身份。

检查点保留节点实体、参数值、`ElementKey`、`LocalState` 和 `ElementTree` 有序层级。
`ViewType` 写出视图参数类型的 GUID，读回时映射到当前进程的函数表编号；
应用可以改变视图注册顺序。读回的每个视图在下一帧重建，覆盖 `Rerun`、`Once`
和 `ChildSource.Caller`，并通过正常构建重新登记依赖。
视图的计算列表、依赖与异步流程由新 World 中的构建重新建立。

整 World 持久化沿用 [[architecture/ECS/Save-Contracts|ECS 存档边界]]：
参与整 World 保存的参数和值组件采用无托管引用的数据。
含 `string` 等托管引用的视图参数可经组件或实体序列化入口使用生成或应用 codec；
长期存档使用显式 schema 与字段 codec。

公开行为验收位于 `CheckpointTests`。`just views-aot win-x64` 发布并执行原生冒烟，
随后以两个进程按相反顺序注册视图，检查检查点读回、下一帧结构与参数、
`Once` 容器输出以及后续依赖变化。临时检查点文件随验收结束清理。

## Relations
[[architecture/someui/Composition|Composition]] · ADR 0011 · ADR 0013
