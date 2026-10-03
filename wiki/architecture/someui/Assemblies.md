# Assemblies

## Current design
| 程序集 | 内容 | 依赖 |
|---|---|---|
| `SomeEngine.Views` | `IView`、`Scope`、`BuildTask`、`VersionCell`、`State<T>`、`ViewRebuildSystem`、`ElementTree` 层级域 | ECS、ECS.Systems、Job、Core（开发检查） |
| `SomeEngine.Views.Generators` | 同名函数生成、`[Composable]` 拦截、`[State]`、`[Component]`、分析器、CS1998 屏蔽、视图类型序列化注册 | — |
| `SomeEngine.Layout` | `ILayout`、`LayoutPass`、`Box`、`LayoutRect`、`LayoutCache`、`Canvas`、`Flex`、`Grid`、`Stack`、`Absolute` | Views |
| `SomeEngine.Animation` | 引擎级动画核心与属性动画（见 [[architecture/Animation|Animation]]）；FLIP 与 `.Animate` 在 Elements 中 | ECS、Job |
| `SomeEngine.Typography` | 字形排版、字体、图集 | — |
| `SomeEngine.Elements` | `Text`、`Image` 等元素、`Surface` 修饰，行为部件，内置控件与复合部件，输入与焦点系统，`.Animate` 衔接 | Views、Layout、Typography、Animation |
| `SomeEngine.Elements.Render` | 提取系统、渲染功能、内置材质 | Elements、Render |
| `SomeEngine.Views.Compose` / `.SwiftUI` / `.Solid` | 各前端的协调原语 | Views |

## Invariants
- `SomeEngine.Elements` 及之后的程序集只使用底层公开 API，不使用 `InternalsVisibleTo` 或任何内部入口。
- 只面向 NativeAOT：不使用反射、`MakeGenericType`、`Activator`、表达式树编译。

## 源生成接入（#19）

`SomeEngine.Views.Generators` 使用 Roslyn 增量生成器、`ViewAnalyzer`
和 `BuildWarningSuppressor` 提供同名函数、调用位置身份、编译期接入检查与 CS1998 屏蔽。
`SomeEngine.Views` 的 NuGet 包将生成器放入 `analyzers/dotnet/cs`，
并通过 `buildTransitive/SomeEngine.Views.props` 启用 `SomeEngine.Views.Generated` 拦截命名空间。
仓库内由 `Directory.Build.props` 与 `Directory.Build.targets` 接入引用 Views 的项目。
视图函数的参数与可见范围见 [[architecture/someui/Views|Views]]；
编译诊断见 [[architecture/someui/Checks|Checks]]。

## Relations
[[architecture/someui/Composition|Composition]] · ADR 0011 · ADR 0013
