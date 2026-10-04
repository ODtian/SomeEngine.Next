# Checks

## Definition
SomeUI 的使用错误通过 [[architecture/validation/Development-Checks|Development Checks]] 报告，shipping 构建中消失。编译期可确定的错误由分析器和编译器报告。

## Current design
运行时开发检查：
- 在计算之外调用作用域函数或修饰。
- 一帧内计算重跑超过迭代上限。
- 未经拦截的节点调用；同 key 兄弟数量变化且带局部状态。
- 对 `Build` 来源的视图调用 `.Content()`，或两个作用域声明同一节点的子节点。
- 同一视图的每次构建以本次计算作用域区分子节点声明，依赖变化引发的再次构建沿正常重跑规则处理。
- 被取消的构建在 `finally` 中调用构建函数。
- 返回 `Task` 的辅助方法在 `await` 之后调用作用域函数。
- `State<T>.Update` 回调中访问同一状态。
- `Build` 与同步完成的 `BuildTask` 抛出的异常（同时写入 `BuildError`）。

构建异常隔离后调用 `Core.Diagnostics.DevelopmentCheck.Report(string)`，
通过应用的 `Trace` listeners 发出错误消息，包含视图类型、实体、帧号及异常。
开发检查在构建期间抛出的使用错误也沿这一边界成为该视图的 `BuildError`。
报告后继续当前帧；节点恢复与错误恢复时机见
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。

编译期：`[Composable]` 与 `[Component]` 用法错误、未开启拦截命名空间、一个函数体混用多个前端的原语、调用 `Remember` 的函数未标记 `[Composable]`、`Build` 中 `ref` 跨越 `await`（编译器）。

## 视图编译诊断（#19）

| 编号 | 级别 | 条件与处理 |
|---|---|---|
| `SEV001` | error | 项目引用 Views 时启用 `InterceptorsNamespaces` 中的 `SomeEngine.Views.Generated`；包的 buildTransitive props 与仓库 props 提供该配置 |
| `SEV002` | info | 省略存储声明时提示选择 `IComponent` 或 `ISparseComponent`，生成器为 partial 视图补充 sparse |
| `SEV003` | error | 生成存储接口需要 partial 视图声明 |
| `SEV004` | error | 普通节点调用应有编译器实际识别的拦截方法；方法组、委托转换或其它生成器产出的调用可改为源码直接调用或显式 key 重载 |
| `SEV005` | error | 嵌套视图函数、Compose 方法与 Solid 导出方法的生成入口需要其包含类型声明为 partial |
| `SEV006` | error | Compose 方法声明为 static |
| `SEV008` | error | Compose 视图、调用位置分组与 Remember 调用应由编译器识别为位置拦截；方法组、委托、取地址与其它生成器产出的调用改为源码直接调用 |
| `SEV009` | error | 调用 Remember 的方法或 lambda 使用自己的 Composable 标记 |
| `SEV013` | info | Compose 方法或 SwiftUI Build 中传入捕获外部变量的 lambda；提示“调用方每次重跑都会分配”，推荐 static lambda 加显式状态参数 |
| `SEV014` | error | Composable 内容参数使用返回 void 或 BuildTask、实参按值传递的委托 |
| `SEV015` | error | SwiftUI 的 State 属性使用实例 partial get/set 属性，位于 partial struct 视图中 |
| `SEV016` | error | 导出的 Solid Component 方法声明为 static |
| `SEV017` | error | 导出的 Solid Component 方法使用 void 或 BuildTask 返回类型，参数按值传递 |
| `SEV018` | error | Compose 或 Build 中使用 Component 时，由编译器识别直接源码调用的位置拦截；方法组、委托转换、取地址与其它生成器产出的调用改为直接源码调用 |
| `SEV019` | error | 按独立函数体确定 Compose、SwiftUI 或 Solid；在其它前端的原语处报告混用错误，组件按视图边界组合 |
| `SEV020` | info | Solid 向其它前端组件传递来自 State 或 Memo 读取的普通值；建议使用 Insert 内容函数或直接传 State |

前端识别、普通值来源与公共编译验收由
[[architecture/someui/Frontends|Frontends]] 维护。

`DiagnosticSuppressor` 根据 `IView.Build` 的实际实现屏蔽 CS1998，包含显式接口实现。
辅助方法的 CS1998 继续由编译器报告。
Compose 生成的 `Build` 直接执行原方法，用户的直接调用沿位置拦截声明子视图。
生成入口的区分与跨程序集规则见 [[architecture/someui/Compose-Frontend|Compose Frontend]]。
绕过编译期拦截检查后直接进入单参数 `Scope.View`，由 Core 的 `DevelopmentCheck`
报告调用位置接入要求；shipping 的条件编译移除该检查并使用 key 0 后备行为。
`Compose.Remember` 的源码调用沿同样的拦截核对与 Core 开发检查边界，
位置、状态保留及回调规则见 [[architecture/someui/Compose-Frontend|Compose Frontend]]。

## Relations
[[architecture/someui/Build-Scope|Build Scope]] · [[architecture/someui/Compose-Frontend|Compose Frontend]]
