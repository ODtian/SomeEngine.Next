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

## Launch 与 Resource（#23）

```csharp
public static class Scope
{
    public static void Launch<TKey>(TKey key, Func<TKey, CancellationToken, BuildTask> run);
    public static Resource<T> Resource<TSource, T>(TSource source, Func<TSource, CancellationToken, ValueTask<T>> fetch);

    public static void Launch<TKey>(TKey key, Func<TKey, CancellationToken, BuildTask> run, ulong callKey);
    public static Resource<T> Resource<TSource, T>(TSource source, Func<TSource, CancellationToken, ValueTask<T>> fetch, ulong callKey);
}

public sealed class Resource<T> : VersionCell
{
    public T Value { get; }
    public bool Loading { get; }
    public Exception? Error { get; }
}
```

`Launch` 是当前视图或计算的异步子计算，首次声明立即调用 `run(key, cancel)`。
生成器为双参数调用提供文件、行和列组成的调用位置 key，三参数重载供调用方显式指定身份。
身份还包含出现次数，并沿 `Scope.Key` 组合列表项的分组。
方法组使用 `callKey` 重载；分析器通过 `SEV004` 指引调用方提供调用身份。
视图或计算重跑时保留再次声明的匹配异步计算及其后代，释放本次省略的异步计算。

任务的 key 使用 `EqualityComparer<TKey>.Default` 比较。相等时保留正在等待或已经完成的任务；
变化时立即触发旧取消令牌，随后启动新任务。创建它的计算释放或视图释放时也触发取消。
每次运行持有自己的作用域和取消源，旧状态机沿现有重建队列继续执行 `finally` 与 `using`。
同一视图的主体构建与多个子任务分别等待、恢复和完成，恢复位置沿本页的重建阶段规则。
任务中的直接读取取得当前值，任务重新开始由传入的 key 决定；需要响应式求值时，
在 `Compute` 中读取来源并声明 `Launch`。建议传 `static` lambda，通过 key 传入所需数据。

`Resource` 在同一调用身份保留一个 `Resource<T>`，来源使用
`EqualityComparer<TSource>.Default` 比较。首次请求以 `default(T)` 作为值并设置 `Loading=true`。
来源变化时取消旧请求、保留最近一次成功的值、清除旧错误并设置 `Loading=true`。
当前请求完成后提交新值并设置 `Loading=false`；失败时保留旧值、记录原异常并结束加载。
后续来源变化重新请求，成功结果同时清除错误。已取消请求的恢复继续使用自己的令牌完成清理，
资源状态由当前请求提交。同步完成的 `ValueTask<T>` 在声明位置直接提交结果。

三个属性的读取都登记该资源的对象依赖。资源持有值、加载状态、错误和版本；
属性读取与整次结果写入通过同一把锁保持完整值，状态改变推进版本，使仅依赖 `Error`
或 `Loading` 的计算也能更新。请求错误作为资源数据供界面读取，`Launch` 的任务异常
沿构建错误入口记录为所属视图的 `BuildError`。

`Rerun` 视图可直接读取来源并调用 `Resource`。`Once` 视图在 `Compute` 中读取来源和
资源属性，使来源变化与请求完成分别更新该计算，旧值沿同一资源保留。
作用域外创建、空回调与取消清理期间创建通过 Core 的 `DevelopmentCheck` 报告。

实现：[Scope 异步入口](../../../src/SomeEngine.Views/Scope.Async.cs)、
[异步子计算](../../../src/SomeEngine.Views/AsyncComputation.cs)、
[资源状态](../../../src/SomeEngine.Views/Resource.cs)。
公开行为验收：[LaunchTests](../../../tests/SomeEngine.Views.Tests/LaunchTests.cs)、
[ResourceTests](../../../tests/SomeEngine.Views.Tests/ResourceTests.cs) 与
[共享异步检查](../../../tests/SomeEngine.Views.Tests/AsyncPrimitiveChecks.cs)，
后者通过 `just ecs-aot win-x64` 编入原生消费者。

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
组件值与子视图参数通过同一个恢复入口写回：与当前值相等时覆盖存储并保留写版本，
值变化时使用 ECS 的整值替换推进写版本。已经在挂起帧中读取并呈现这些输出的视图与计算，
因此沿既有依赖检查更新为恢复后的源值。失败与取消共用这一恢复规则。
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
旧清理中的读取按仅取值处理，暂存的组件副本随清理结束释放，当前构建继续维护自己的依赖和输出。

`BuildTask` 方法由 [[architecture/someui/Assemblies|Assemblies]] 中的编译阶段补齐 await 取消边界。
方法构建器在消费等待结果前观察取消，在消费后、继续业务代码前抛出 `OperationCanceledException`。
这条路径覆盖任意 `Task`、`ValueTask`、`JobHandle` 和用户等待类型，等待结果的正常消费
同时完成可复用等待来源的释放。每个方法构建器记录已经观察的取消，`finally` 中后续
`Task`/`ValueTask` await 可以继续完成清理；等待对象自身抛出取消异常时也建立该记录。
`BuildTask` awaiter 在消费辅助方法结果时观察所属构建的取消令牌，并抛出 `OperationCanceledException`；
`Scope.NextFrame()` 的 awaiter 在逐帧恢复点执行同一取消检查。
异步辅助方法成功返回后，等待它的已取消构建也由 `BuildTask` awaiter 抛出取消异常。
方法内的 `finally` 和 `using` 沿 C# 状态机的正常异常路径执行。
重建入口把已取消构建的 `OperationCanceledException` 作为结束消费，`BuildError` 继续表示构建失败。

取消源汇总的回调异常由取消入口交给既有 `BuildError` 与开发报告边界消费。
构建恢复记录完成还原，新构建立即开始，同帧其余视图和计算继续更新。

`Once` 构建按原有规则直接取值，依赖写入后继续等待；视图释放会触发它的取消令牌。
World 的 `EntityDestroying` 通知在节点释放时立即发出取消，旧构建随后使用保存的作用域恢复清理。
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
`BuildResumeChecks.cs` 在两种 World 上验证旧等待完成后保留新 State、取消异常、
finally/using 与异步清理、回调异常隔离和 Launch 错误恢复，并同时编入 Views 与 ECS NativeAOT 入口。

## Relations
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0012
