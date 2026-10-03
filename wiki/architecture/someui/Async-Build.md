# Async Build

## Definition
`Build` 返回 `BuildTask`，可以使用真正的 `async`/`await`。`BuildTask` 的方法构建器接管恢复位置：等待对象完成后只把（视图，状态机）放入队列，视图重建系统在重建阶段恢复它并设置作用域。

## Invariants
- 恢复只发生在 UI 线程的重建阶段，world 由该阶段独占；同一帧恢复的视图按层级深度与实体排序，结果不依赖线程时序。
- 不依赖 `SynchronizationContext`；被等待的库内部使用 `ConfigureAwait(false)` 不影响恢复位置。
- 同步完成零分配；首次挂起时状态机装箱一次，按状态机类型池化。
- `await` 之前声明的节点立即可见；整个构建成功完成后统一清理剩余子节点。取消与重新开始的目标语义由 #21 实现：`Rerun` 构建挂起期间依赖变化，或视图被释放时，在恢复点抛出 `OperationCanceledException`，执行 `finally` 与 `using`，触发 `Scope.Cancel` 后重新开始。
- 调用作用域函数的异步辅助方法必须返回 `BuildTask`；返回 `Task` 的方法在 `await` 后调用作用域函数由开发检查报告。
- 同步视图也写作 `async BuildTask Build()`，CS1998 由 `DiagnosticSuppressor` 屏蔽。

## 后续能力
- #21 提供构建取消与依赖变化后的重启。
- #22 提供 `JobHandle.GetAwaiter()`，基于 `JobSystem.OnCompleted`，放在 Job 程序集，作为通用能力。
- `Launch(key, static async (k) => ...)`：后台任务，key 变化时取消并重新开始。
- `Resource<T>`：值、加载中、错误三部分组成的状态；来源变化时重新请求，期间保留旧值。

## 方法构建器与恢复（#6、#20）

```csharp
[AsyncMethodBuilder(typeof(BuildTaskBuilder))]
public readonly struct BuildTask
{
    public BuildTaskAwaiter GetAwaiter();
}

public static class Scope
{
    public static FrameAwaitable NextFrame();
}
```

`BuildTaskBuilder` 直接驱动同步状态机。已经完成的 `Task`、`ValueTask` 和
`BuildTask` await 在当前作用域内继续执行；成功同步完成的构建结果是值类型，
方法构建器在这条路径上使用零托管分配。Release 中的公开分配测试先预热，再用
`GC.GetAllocatedBytesForCurrentThread` 验证连续 10,000 次同步完成构建的差值为 `0`。
C# 编译器在 Release 中生成 struct 状态机，Debug 中的 async 状态机为 class。

首次挂起时，方法构建器把状态机保存到按状态机类型、按线程复用的托管对象中。
同一个异步方法的后续挂起继续使用该对象。对象保存构建作用域和完成结果；
异步辅助方法返回自己的 `BuildTask`，并与调用者共用本次视图构建的作用域数据。
每份挂起结果由调用者 await 一次，或作为视图 `Build` 的返回值交给构建入口消费；
消费时传播异常并归还状态机对象。

`AwaitOnCompleted` 与 `AwaitUnsafeOnCompleted` 把登记就绪项的回调交给等待对象。
回调可由任意线程执行，仅通过锁把状态机与它的视图作用域加入该 World 的就绪队列。
恢复位置由 [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] 定义。
队列和挂起构建表由该 World 的重建阶段持有，作用域的子声明位置、key 分组和
读取登记状态保留到整个构建完成。挂起期间保持这次构建，完成后再沿既有版本检查处理变化。

`Scope.NextFrame()` 返回 `FrameAwaitable`。当前重建阶段内注册的回调在该 World
下一次重建开始时登记就绪项，每次 await 推进一帧。同一帧恢复期间新就绪的
`BuildTask` 调用者在后续帧恢复，因此挂起的辅助方法完成后，外层构建在下一次
重建阶段继续执行。同步完成的辅助方法沿同步 await 路径当场返回。

恢复完成的通知在退出作用域后发出。返回 `Task` 的辅助方法挂起后调用 `Scope`
时，由 `Core.Diagnostics.DevelopmentCheck` 报告所需的活动构建作用域；
返回 `BuildTask` 的辅助方法由重建阶段恢复作用域。构建异常沿构建入口传播，
作用域在异常路径恢复，其余已经就绪的视图保留到后续帧执行。

实现：[BuildTask](../../../src/SomeEngine.Views/BuildTask.cs)、
[逐帧等待](../../../src/SomeEngine.Views/FrameAwaitable.cs)、
[World 构建队列](../../../src/SomeEngine.Views/BuildQueue.cs)。
公开行为验收：[AsyncBuildTests](../../../tests/SomeEngine.Views.Tests/AsyncBuildTests.cs)。
共享公开检查 `FirstBuildChecks.cs` 同时覆盖 `NextFrame` 的 30 帧序列与
工作线程完成 `ConfigureAwait(false)` 后的内容更新，经现有 `just ecs-aot win-x64` 入口运行。

## Relations
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0012
