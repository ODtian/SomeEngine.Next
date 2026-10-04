# Async Build

## Definition
`Build` 返回 `BuildTask`，可以使用真正的 `async`/`await`。`BuildTask` 的方法构建器接管恢复位置：等待对象完成后只把（视图，状态机）放入队列，视图重建系统在重建阶段恢复它并设置作用域。

## Invariants
- 恢复只发生在 UI 线程的重建阶段，world 由该阶段独占；同一帧恢复的视图按层级深度与实体排序，结果不依赖线程时序。
- 不依赖 `SynchronizationContext`；被等待的库内部使用 `ConfigureAwait(false)` 不影响恢复位置。
- 同步完成零分配；首次挂起时状态机装箱一次，按状态机类型池化。
- `await` 之前声明的节点立即可见；整个构建成功完成后统一清理剩余子节点。`Rerun` 构建挂起期间参数或依赖变化时，触发这次构建的 `Scope.Cancel`，并在同一重建阶段重新开始。视图释放时同样触发取消；旧构建在等待对象就绪后的恢复点执行 `finally` 与 `using` 清理。
- 调用作用域函数的异步辅助方法必须返回 `BuildTask`；返回 `Task` 的方法在 `await` 后调用作用域函数由开发检查报告。
- 同步视图也写作 `async BuildTask Build()`，CS1998 由 `DiagnosticSuppressor` 屏蔽。

## 后续能力
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
    public static CancellationToken Cancel { get; }
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
异步辅助方法返回自己的 `BuildTask`，并保存挂起时使用的声明上下文。
视图构建持有主作用域和需跨帧保留的内容作用域，辅助方法恢复后继续向原父节点声明。
每份挂起结果由调用者 await 一次，或作为视图 `Build` 的返回值交给构建入口消费；
消费时传播异常并归还状态机对象。

`AwaitOnCompleted` 与 `AwaitUnsafeOnCompleted` 把登记就绪项的回调交给等待对象。
回调可由任意线程执行，仅通过锁把状态机与它的视图作用域加入该 World 的就绪队列。
恢复位置由 [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] 定义。
队列和挂起构建表由该 World 的重建阶段持有，作用域的子声明位置、key 分组和
读取登记状态保留到整个构建完成。挂起期间沿既有版本检查处理变化，失效时执行下文的取消与重启。
构建成功完成时统一清理主作用域及保留的内容作用域，并提交开始构建时的参数版本。

`Scope.Write` 的副本由本次计算的主作用域持有，内容作用域与辅助方法共享它。
初次挂起返回构建入口、每次恢复再挂起以及成功完成时，使用现有 `ComponentCopy.Commit`
比较当前 ECS 值并提交本段写入，随后清空副本集合。下一段首次写入从当前 ECS 数据取值。
子声明数据与读取依赖跨段保留，整个构建成功完成时统一执行子树收尾。
每次提交沿现有子声明变更记录保存原值和组件存在性。整个构建失败时，
按提交的逆序恢复先前输出，并恢复该视图之前的计算列表；异常段的副本随后释放。
嵌套异步子视图与同步父视图共享的计算恢复记录按使用它的构建计数保留，
最后一个构建完成时释放原计算，恢复时把原计算列表交回视图。

恢复时推进对象依赖集合的读取帧，保留集合中已登记的版本，沿
`DependencyChanges` 的轮次检查处理同帧收敛。挂起构建中的 `Compute` 与 `Memo`
继续由同一个重建系统检查；恢复后的计算追加到该视图现有列表。
从存档读取的 `ViewType.RebuildRequired` 通过现有构建入口消费，
`Once` 视图也重新启动其异步构建；Rerun 参数版本在整个构建结束时建立或更新。

`Scope.NextFrame()` 返回 `FrameAwaitable`。当前重建阶段内注册的回调在该 World
下一次重建开始时登记就绪项，每次 await 推进一帧。同一帧恢复期间新就绪的
`BuildTask` 调用者在后续帧恢复，因此挂起的辅助方法完成后，外层构建在下一次
重建阶段继续执行。同步完成的辅助方法沿同步 await 路径当场返回。

恢复完成的通知在退出作用域后发出。返回 `Task` 的辅助方法挂起后调用 `Scope`
时，由 `Core.Diagnostics.DevelopmentCheck` 报告所需的活动构建作用域；
返回 `BuildTask` 的辅助方法由重建阶段恢复作用域。异步辅助方法的异常由 await 传播，
视图构建异常沿 #18 的处理写入 `BuildError`，记录异常和当前重建帧，恢复该视图原有子声明。
作用域在异常路径恢复，同帧其余就绪视图继续执行。嵌套子视图首次挂起时保留自己的
子声明变更，以便在外层同步构建结束后独立完成或恢复。

## 取消与重启（#21）

每次视图构建的主作用域持有自己的取消源，在首次读取 `Scope.Cancel` 或触发取消时建立。
内容作用域和返回 `BuildTask` 的异步辅助方法共用这份取消源。把 `Scope.Cancel` 传给
I/O 的取消参数，或用 `task.WaitAsync(Scope.Cancel)` 等待外部任务，可在取消时使等待对象就绪。
外部等待对象仍通过原有就绪回调安排恢复，清理继续由 World 的重建阶段执行。

每帧恢复就绪项前先检查挂起构建的参数及读取依赖，同帧后续收敛轮次继续复核。
取消时先恢复这次构建已提交的子声明、组件副本及原计算，再启动新构建。
旧构建保留自己的状态机和作用域，直到等待对象就绪并完成清理；
旧构建的最后收尾只消费自己的结果与作用域，新构建继续持有当前视图的输出及参数版本。

`BuildTask` awaiter 在消费结果时观察所属构建的取消令牌，并抛出 `OperationCanceledException`；
`Scope.NextFrame()` 的 awaiter 在逐帧恢复点执行同一取消检查。
异步辅助方法成功返回后，等待它的已取消构建也由 `BuildTask` awaiter 抛出取消异常。
方法内的 `finally` 和 `using` 沿 C# 状态机的正常异常路径执行。
重建入口把已取消构建的 `OperationCanceledException` 作为结束消费，`BuildError` 继续表示构建失败。

`Once` 构建按原有规则直接取值，依赖写入后继续等待；视图释放会触发它的取消令牌。
`ViewType` 的 ECS 销毁钩子在节点释放时立即发出取消，旧构建随后使用保存的作用域恢复清理。
取消清理中再次声明视图、调用 `Compute`、`Memo` 或打开内容作用域时，
通过 Core 的 `DevelopmentCheck` 报告；检查调用随 shipping 编译移除。

公开行为验收：[BuildCancellationTests](../../../tests/SomeEngine.Views.Tests/BuildCancellationTests.cs)。

实现：[BuildTask](../../../src/SomeEngine.Views/BuildTask.cs)、
[逐帧等待](../../../src/SomeEngine.Views/FrameAwaitable.cs)、
[World 构建队列](../../../src/SomeEngine.Views/BuildQueue.cs)。
公开行为验收：[AsyncBuildTests](../../../tests/SomeEngine.Views.Tests/AsyncBuildTests.cs)。
共享公开检查 `FirstBuildChecks.cs` 同时覆盖 `NextFrame` 的 30 帧序列与
工作线程完成 `ConfigureAwait(false)` 后的内容更新，经现有 `just ecs-aot win-x64` 入口运行。
`AsyncIntegrationChecks.cs` 同时编入这一入口，覆盖 table/sparse 分段提交、
挂起期间 Memo 绑定、跨段失败时的组件与原计算恢复、对象读取帧与存档后的异步构建恢复。

## Relations
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0012
