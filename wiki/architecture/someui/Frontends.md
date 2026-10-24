# Frontends

## Definition
前端是在 C# 中借鉴 Compose、SwiftUI、SolidJS 的写法。各前端只在协调方式上不同：

| 前端 | 构建模式 | 节点身份 | 变化如何送达 |
|---|---|---|---|
| Compose | `Rerun` | 调用位置 | 重跑后比较参数 |
| SwiftUI | `Rerun` | 调用位置 | 重跑后比较参数 |
| Solid | `Once` | 创建时确定 | 计算直接写目标字段 |

节点身份规则（调用位置）、元素、修饰、布局、输入、环境数据、动画、异步（`BuildTask`、`Launch`、`Resource<T>`）、状态 `State<T>`、`Key` 与错误处理只有一套，所有前端共用，不为还原原版语法另做别名。

## Invariants
- 状态完全通用：各前端读写同一种 `State<T>`，跨前端的依赖正确失效。
- 组件在边界处混用：每个前端的组件在其它前端看来都是视图。
- 一个函数体内只使用一种前端的原语，违反时由分析器报告错误。
- 从 Solid 组件向其它前端组件传普通值只求值一次；需要随数据更新时使用 `Insert(() => ...)` 或传 `State<T>`。
- 结构嵌套统一使用 `using (entity.Content())`。

`[Component]` 的 Once 视图导出与 `Insert(Action)` 的 Rerun 子视图实现见
[[architecture/someui/Solid-Frontend|Solid Frontend]]。

## 混用规则分析器（#64）

`SomeEngine.Views.Generators.FrontendAnalyzer` 按独立函数体确定前端：
方法或 lambda 的 `[Composable]` 标记选择 Compose；带 `[State]` 属性的视图实际实现的
`IView.Build` 选择 SwiftUI，包含显式接口实现；函数体直接调用 Solid 原语，
或通过 `[Component]` 导出 Once 视图时选择 Solid。
lambda、匿名委托和局部函数分别检查自己的函数体。

原语按编译器绑定到的类型与成员识别。Compose 的 `Remember`、`MutableStateOf`，
SwiftUI 的 `[State]` 属性，以及 Solid 的协调原语、getter 文本和可响应修饰参与混用检查。
组件通过 `[Composable]` 方法、生成的同名视图函数与 `Scope.View` 组合；
共享的 `State<T>`、`Binding<T>`、作用域函数、普通元素和修饰继续按各自公开 API 使用。
函数体使用其它前端原语时，`SEV019` 在该原语的源码位置报告 error。
方法组赋给局部委托或作为参数传递时，在该方法引用处执行同一检查。
`nameof` 继续用于取得名称，lambda 与局部函数继续按自己的函数体确定前端。

Solid 向 Compose 组件或编译期可确定为 `Rerun` 的视图传递普通值时，分析器追踪
`State<T>.Value`、`Memo<T>.Value` 与 `Solid.Memo` getter 的读取。
局部值与函数参数、算术与转换、赋值、分支和循环沿编译器控制流传递数据来源。
同名函数的参数、`Scope.View` 中视图的构造参数与对象初始化器字段均在公开调用处检查，
包括命名实参和显式 key 重载。
显式 `Mode => BuildMode.Rerun` 与静态接口 Mode 声明按其所属文件的语义模型求值，
跨文件调用采用同一普通值提示规则。
来源包含这些读取的普通值由 `SEV020` 发出 info，建议在组件调用外使用
`Insert(() => ...)`，把读取放在该内容函数中；也可直接传递 `State<T>`。
状态引用、getter 参数和响应式计算中的读取沿各自的更新方式使用。

公开编译验收见 `tests/SomeEngine.Views.Generators.Tests/FrontendTests.cs`，通过
Roslyn 的 `DiagnosticAnalyzer` 接口观察诊断编号、级别、消息和源码位置。
`Show` 的用例以本页与 Solid 原子页约定的公开签名提供编译输入。
共同编译诊断见 [[architecture/someui/Checks|Checks]]。

## Relations
[[architecture/someui/Compose-Frontend|Compose]] · [[architecture/someui/SwiftUI-Frontend|SwiftUI]] · [[architecture/someui/Solid-Frontend|Solid]] · ADR 0009
