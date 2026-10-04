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
- `Launch(key, static async (k) => ...)`：后台任务，key 变化时取消并重新开始。
- `Resource<T>`：值、加载中、错误三部分组成的状态；来源变化时重新请求，期间保留旧值。

## 等待 Job（#22）

`SomeEngine.Job` 提供通用的 `JobHandle` await 扩展：

```csharp
public static class JobHandleAwaitExtensions
{
    public static JobHandleAwaiter GetAwaiter(this JobHandle handle);
}

public readonly struct JobHandleAwaiter : ICriticalNotifyCompletion
{
    public bool IsCompleted { get; }
    public void GetResult();
    public void OnCompleted(Action continuation);
    public void UnsafeOnCompleted(Action continuation);
}
```

`IsCompleted` 读取 `JobHandle.IsCompleted`，已完成的 Job 和默认句柄在当前调用位置继续。
挂起等待通过 `JobSystem.OnCompleted` 登记回调，回调按已有语义在登记、完成或外部信号线程
内联执行。`GetResult` 调用 `JobHandle.Complete()`，沿其既有的首次错误观察语义取得结果。
调度、依赖、资源访问和句柄完成状态继续由 Job 系统持有。

`Build` 中的 `await handle` 由 `BuildTaskBuilder` 接管恢复：Job 完成回调将状态机放入
该 World 的重建队列，重建阶段设置原构建作用域后继续读取 Job 写入的数据，并提交界面输出。
已完成的 Job 沿同步 await 路径在当前重建阶段继续执行。

实现：[JobHandleAwaitExtensions](../../../src/SomeEngine.Job/JobHandleAwaitExtensions.cs)。
公开行为验收：[JobHandleAwaitTests](../../../tests/SomeEngine.Job.Tests/JobHandleAwaitTests.cs)
覆盖已完成与挂起 Job 的结果读取、完成线程上的异步恢复、默认句柄、Job 错误传播，以及
完成发生于判断和登记之间时在登记线程恢复的行为。
[JobBuildTests](../../../tests/SomeEngine.Views.Tests/JobBuildTests.cs) 通过公开视图、World 和
无头帧入口显示 Job 写入的值，验证加载值保留到重建阶段，以及同步与挂起等待的恢复线程。
共享场景 `JobBuildChecks.cs` 同时编入现有 `just ecs-aot win-x64` 入口。

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
异步辅助方法返回自己的 `BuildTask`，并保存挂起时使用的声明上下文。
视图构建持有主作用域和需跨帧保留的内容作用域，辅助方法恢复后继续向原父节点声明。
每份挂起结果由调用者 await 一次，或作为视图 `Build` 的返回值交给构建入口消费；
消费时传播异常并归还状态机对象。

`AwaitOnCompleted` 与 `AwaitUnsafeOnCompleted` 把登记就绪项的回调交给等待对象。
回调可由任意线程执行，仅通过锁把状态机与它的视图作用域加入该 World 的就绪队列。
恢复位置由 [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] 定义。
队列和挂起构建表由该 World 的重建阶段持有，作用域的子声明位置、key 分组和
读取登记状态保留到整个构建完成。挂起期间保持这次构建，完成后再沿既有版本检查处理变化。
构建成功完成时统一清理主作用域及保留的内容作用域，并提交开始构建时的参数版本。

`Scope.Write` 的副本由本次计算的主作用域持有，内容作用域与辅助方法共享它。
初次挂起返回构建入口、每次恢复再挂起以及成功完成时，使用现有 `ComponentCopy.Commit`
比较当前 ECS 值并提交本段写入，随后清空副本集合。下一段首次写入从当前 ECS 数据取值。
子声明数据与读取依赖跨段保留，整个构建成功完成时统一执行子树收尾。
每次提交沿现有子声明变更记录保存原值和组件存在性。整个构建失败时，
按提交的逆序恢复先前输出，并恢复该视图之前的计算列表；异常段的副本随后释放。
嵌套异步子视图与同步父视图共享的计算恢复记录按使用它的构建计数保留，
最后一个构建完成时释放原计算，恢复时把原计算列表交回视图。

声明式动画目标随每段提交进入属性动画公开 API。目标恢复记录沿同一子声明数据路径
保留、转移与释放；构建失败后，动画从当帧呈现值继续向此前完整构建的目标过渡。
字段目标与呈现值的规则见 [[architecture/someui/Animation|SomeUI Animation]]。

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
