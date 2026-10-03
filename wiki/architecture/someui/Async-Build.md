# Async Build

## Definition
`Build` 返回 `BuildTask`，可以使用真正的 `async`/`await`。`BuildTask` 的方法构建器接管恢复位置：等待对象完成后只把（视图，状态机）放入队列，视图重建系统在重建阶段恢复它并设置作用域。

## Invariants
- 恢复只发生在 UI 线程的重建阶段，world 由该阶段独占；同一帧恢复的视图按层级深度与实体排序，结果不依赖线程时序。
- 不依赖 `SynchronizationContext`；被等待的库内部使用 `ConfigureAwait(false)` 不影响恢复位置。
- 同步完成零分配；首次挂起时状态机装箱一次，按状态机类型池化。
- `await` 之前声明的节点立即可见。`Rerun` 模式的构建挂起期间依赖变化，或视图被释放时，恢复时抛出 `OperationCanceledException`（`finally` 与 `using` 照常执行），并触发 `Scope.Cancel`；随后重新开始。
- 调用作用域函数的异步辅助方法必须返回 `BuildTask`；返回 `Task` 的方法在 `await` 后调用作用域函数由开发检查报告。
- 同步视图也写作 `async BuildTask Build()`，CS1998 由 `DiagnosticSuppressor` 屏蔽。

## Current design
- `JobHandle.GetAwaiter()` 基于 `JobSystem.OnCompleted` 提供，放在 Job 程序集，作为通用能力。
- `NextFrame()` 等待下一次重建阶段。
- `Launch(key, static async (k) => ...)`：后台任务，key 变化时取消并重新开始。
- `Resource<T>`：值、加载中、错误三部分组成的状态；来源变化时重新请求，期间保留旧值。

## 同步完成阶段（#6）

`BuildTask` 标注 `AsyncMethodBuilder(typeof(BuildTaskBuilder))`，当前支持普通同步
`Build` 及同步完成的 `async BuildTask Build()`。已经完成的 `Task`、`ValueTask`
和 `BuildTask` await 在当前构建作用域内继续执行。

方法构建器直接驱动同步状态机，并把同步异常保存在构建结果中。构建入口消费结果时
重新抛出该异常，随后作用域通过 `Dispose` 恢复。真实挂起的 await 调用统一
`DevelopmentCheck.Fail`，报告当前阶段要求同步完成。恢复队列、取消与跨帧 await
由 #20 在这一构建器与重建阶段接缝中继续实现。

## Relations
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0012
