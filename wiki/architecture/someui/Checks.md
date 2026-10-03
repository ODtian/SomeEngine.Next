# Checks

## Definition
SomeUI 的使用错误通过 [[architecture/validation/Development-Checks|Development Checks]] 报告，shipping 构建中消失。编译期可确定的错误由分析器和编译器报告。

## Current design
运行时开发检查：
- 在计算之外调用作用域函数或修饰。
- 一帧内计算重跑超过迭代上限。
- 未经拦截的节点调用；同 key 兄弟数量变化且带局部状态。
- 对 `Build` 来源的视图调用 `.Content()`，或两个作用域声明同一节点的子节点。
- 同一视图开始新一次构建时重置该视图的子节点声明记录，依赖变化引发的再次构建沿正常重跑规则处理。
- 被取消的构建在 `finally` 中调用构建函数。
- 返回 `Task` 的辅助方法在 `await` 之后调用作用域函数。
- `State<T>.Update` 回调中访问同一状态。
- `Build` 抛出的异常（同时写入 `BuildError`）。

编译期：`[Composable]` 用法错误、未开启拦截命名空间、一个函数体混用多个前端的原语、调用 `Remember` 的函数未标记 `[Composable]`、`Build` 中 `ref` 跨越 `await`（编译器）。

## 视图编译诊断（#19）

| 编号 | 级别 | 条件与处理 |
|---|---|---|
| `SEV001` | error | 项目引用 Views 时启用 `InterceptorsNamespaces` 中的 `SomeEngine.Views.Generated`；包的 buildTransitive props 与仓库 props 提供该配置 |
| `SEV002` | info | 省略存储声明时提示选择 `IComponent` 或 `ISparseComponent`，生成器为 partial 视图补充 sparse |
| `SEV003` | error | 生成存储接口需要 partial 视图声明 |
| `SEV004` | error | 普通节点调用应有编译器实际识别的拦截方法；方法组、委托转换或其它生成器产出的调用可改为源码直接调用或显式 key 重载 |
| `SEV005` | error | 嵌套视图的同名函数需要外层类型声明为 partial |

`DiagnosticSuppressor` 根据 `IView.Build` 的实际实现屏蔽 CS1998，包含显式接口实现。
辅助方法的 CS1998 继续由编译器报告。
绕过编译期拦截检查后直接进入单参数 `Scope.View`，由 Core 的 `DevelopmentCheck`
报告调用位置接入要求；shipping 的条件编译移除该检查并使用 key 0 后备行为。

## Relations
[[architecture/someui/Build-Scope|Build Scope]] · [[architecture/someui/Compose-Frontend|Compose Frontend]]
