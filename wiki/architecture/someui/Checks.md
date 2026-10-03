# Checks

## Definition
SomeUI 的使用错误通过 [[architecture/validation/Development-Checks|Development Checks]] 报告，shipping 构建中消失。编译期可确定的错误由分析器和编译器报告。

## Current design
运行时开发检查：
- 在计算之外调用作用域函数或修饰。
- 一帧内计算重跑超过迭代上限。
- 未经拦截的节点调用；同 key 兄弟数量变化且带局部状态。
- 对 `Build` 来源的视图调用 `.Content()`，或两个作用域声明同一节点的子节点。
- 被取消的构建在 `finally` 中调用构建函数。
- 返回 `Task` 的辅助方法在 `await` 之后调用作用域函数。
- `State<T>.Update` 回调中访问同一状态。
- `Build` 抛出的异常（同时写入 `BuildError`）。

编译期：`[Composable]` 用法错误、未开启拦截命名空间、一个函数体混用多个前端的原语、调用 `Remember` 的函数未标记 `[Composable]`、`Build` 中 `ref` 跨越 `await`（编译器）。

## Relations
[[architecture/someui/Build-Scope|Build Scope]] · [[architecture/someui/Compose-Frontend|Compose Frontend]]
