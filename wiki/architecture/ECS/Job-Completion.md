# Job 状态与完成语义  [[architecture/ECS-Architecture|ECS Architecture]]


```text
Created
  -> PinnedWaitingSemantic
  -> AdmittingFixed | PreparingTopology -> Sealing
  -> WaitingHazards
  -> Ready
  -> Running
  -> WorkReleasing
  -> WorkReleased
  -> WaitingDescendants
  -> ScopeCompleted
```

## 读取完成结果

`JobHandle.Complete()` 等待工作、子工作与显式依赖结束，并读取同一份成功或失败。
完成记录仍有效时，重复读取及已登记的多个等待者取得相同结果。异常类型及传播关系由
原始失败决定；调度器关闭时的错误汇报去重单独记录。

成功记录在完成分派结束后回收。失败记录在原始失败或其传播结果被观察、活动等待与完成通知
结束后，可供后续提交复用。原始 `JobHandle` 的失败观察期随该记录复用结束，复用后的
原始句柄继续表示已完成。

跨后续调度共享结果时，在原始记录有效期间调用 `WaitAsync()`，保存返回的 `ValueTask`
或其 `AsTask()`。该结果支持重复和并发等待，并独立保留已交付的失败。
`GetAwaiter()` 同样取得本次等待的完成结果；通知就绪后，即使消费者延后执行 `GetResult()`，
仍取得这次等待保存的终态。底层完成记录在通知交付后继续按上述规则复用。

源码入口：[JobHandle](../../../src/SomeEngine.Job/JobHandle.cs)、
[异步等待](../../../src/SomeEngine.Job/JobHandleAwaitExtensions.cs)、
[完成记录](../../../src/SomeEngine.Job/Scheduler.CompletionStore.cs) 与
[错误汇报](../../../src/SomeEngine.Job/Scheduler.Failures.cs)。
公开行为检查：[CompletionStateTests](../../../tests/SomeEngine.Job.Tests/CompletionStateTests.cs)、
[JobHandleAwaitTests](../../../tests/SomeEngine.Job.Tests/JobHandleAwaitTests.cs)。

## 外部来源与异步等待

本节记录已实现的公共接口与使用方法。`Scheduler` 的入口为：

```csharp
JobHandle CreateExternalFenceHandle(Task task, ReadOnlySpan<JobResourceAccess> accesses = default);
JobHandle CreateExternalFenceHandle(ValueTask task, ReadOnlySpan<JobResourceAccess> accesses = default);
```

`JobSystem` 提供绑定默认 Scheduler 的同名入口。`Task<T>` 可直接交给 Task 入口，
其返回值继续由原 Task 持有。`ValueTask` 入口调用 `AsTask()` 消费来源一次；
带返回值的 `ValueTask<T>` 由调用方先转成 Task，再保留该 Task 读取返回值。

来源进入终态后，桥接先调用 `IJobExternalFence.GetResult()`，再结束对应 Job 的外部工作。
Task 提供者通过自己的 awaiter 取得来源结果，失败或取消沿 Job 原有的父子工作和显式依赖
传播关系交付。普通 fence 的默认 `GetResult()` 表示成功，提供者可实现该方法交付具体终态。
`accesses` 沿原 fence 入口保留资源，Job 完成点覆盖来源结果消费和正常资源释放。

`JobHandle.WaitAsync()` 返回可重复和并发等待的 `ValueTask`，转换成 Task 后同样保留共享
结果；取消终态交付为取消任务。`WaitAsync(CancellationToken)` 在共享结果上建立一次独立
等待，令牌取消时结束这次等待。共享工作、其他等待者及资源继续保持到原工作完成。
普通 Task 消费者沿 .NET 的异步恢复规则继续执行，`await handle` 沿既有完成通知线程恢复。
SomeUI 的恢复由 [[architecture/someui/Async-Build|BuildTaskBuilder 与重建阶段]] 接管。

源码入口：[外部完成提供者](../../../src/SomeEngine.Job/IJobExternalFence.cs)、
[来源桥接](../../../src/SomeEngine.Job/Scheduler.Fence.cs)、
[默认入口](../../../src/SomeEngine.Job/JobSystem.cs) 与
[异步等待](../../../src/SomeEngine.Job/JobHandle.cs)。
公共行为检查：[ExternalCompletionTests](../../../tests/SomeEngine.Job.Tests/ExternalCompletionTests.cs)、
[ExternalFenceBridgeTests](../../../tests/SomeEngine.Job.Tests/ExternalFenceBridgeTests.cs) 与
[SomeUI Job 消费](../../../tests/SomeEngine.Views.Tests/JobBuildTests.cs)。

## 带资源的终态清理

本节记录已实现的公共接口与使用方法。`Scheduler` 和默认 Scheduler 的 `JobSystem`
都提供以下入口，原有三参数入口使用空访问集合：

```csharp
JobHandle ScheduleFinally<T>(in T job, ReadOnlySpan<JobResourceAccess> accesses,
    JobScheduleOptions options, JobHandle dependency) where T : struct, IJob;
```

调用方在提交清理时取得 `dependency.GetAwaiter()`，将这份结果与待归还的输入、缓冲区
一起保存到清理 `IJob` 的字段中。前置工作成功、失败或取消后，清理进入正常资源激活与冲突
等待。`accesses` 表达清理实际读写的资源和范围，其保留、访问与释放沿普通调度路径处理。

清理回调先恢复数据或归还缓冲，再调用保存结果的 `GetResult()`。该调用读取前置终态，
原完成记录在清理等待资源期间复用后，保存的结果继续交付原失败。清理自身的异常沿同一
Job 完成路径记录。清理可以通过正常调度创建子工作，返回的完成句柄覆盖清理本体和全部
子工作结束；消费者在这个完成点之后读取结果或回收输入。

实现链路为 `ScheduleFinally` → `ScheduleCore` 的终态依赖策略 → 前置完成 → 资源激活
及冲突等待 → `IJob.Execute()` → 工作释放与子工作汇合 → 最终完成结果。终态依赖策略沿
已有依赖通知传递，前置状态与资源数据继续由各自原持有者维护。

源码入口：[清理调度](../../../src/SomeEngine.Job/Scheduler.cs)、
[依赖与资源激活](../../../src/SomeEngine.Job/Scheduler.Dependency.cs)、
[结果持有](../../../src/SomeEngine.Job/JobHandleAwaitExtensions.cs)。
公共行为检查：[CleanupResourceTests](../../../tests/SomeEngine.Job.Tests/CleanupResourceTests.cs)、
[DependencyTests](../../../tests/SomeEngine.Job.Tests/DependencyTests.cs) 与
[ResourceLifetimeTests](../../../tests/SomeEngine.Job.Tests/ResourceLifetimeTests.cs)。


## 合并依赖结果

`Scheduler.CombineDependencies` 从输入收集尚待完成的前置工作和已经完成的失败。小输入使用
内联句柄数组；较大输入租用句柄数组，按所属 Scheduler 内的记录编号和版本排序，再线性
压缩重复项，去重工作量为 `O(N log N)`。临时数组归还时清理实际收集范围。

结果只由一个尚待完成的前置提供时，直接返回其 `JobHandle`。多个前置或额外的已完成失败
继续由合并完成记录交付；后续消费者等待全部实际前置，并取得合并结果中的失败。完整依赖
登记沿既有完成关系维护祖先和循环约束。

本节属于已实现的公共接口行为。源码入口：
[依赖合并](../../../src/SomeEngine.Job/Scheduler.Dependency.cs)；公共行为检查：
[合并结果与消费者](../../../tests/SomeEngine.Job.Tests/DependencyMergeTests.cs)、
[依赖完成](../../../tests/SomeEngine.Job.Tests/DependencyTests.cs)。固定递增规模的完整合并与
完成测量见 [Job benchmark](../../../tools/SomeEngine.Job.Benchmarks/README.md) 的 `--dependencies`。

## 同步范围执行

`Scheduler.ExecuteParallel(in job, length, batchSize, accesses, options)` 消费具体的
`IJobParallelFor` 工作，`Execute(index)` 读取调用方持有的范围或数据。省略 `accesses` 的重载
接受相同的工作、长度、批大小和 `JobScheduleOptions`。显式批大小沿用每批一份工作值副本；
`AutomaticBatchSize` 使用现有类型化批大小选择器。

调用线程保留首批并继续动态领取，其余参与者进入 Scheduler 的共享就绪队列。同步批次持有
领取位置、首个失败和参与者完成数；队列完成本次 drainer 的清理后才登记参与者退出。
调用方等全部参与者退出，再归还类型化批次并取得结果；范围执行失败同样在这个完成点抛出。
批次由发起线程的类型化空闲链复用。

根调用的输入和输出由调用方保持到同步返回。声明访问时，已有同步资源入口先取得访问，
所有参与者使用该访问 scope；本体退出后释放访问，并完成该 scope 的子工作。根调用省略访问时，
同步批次直接持有参与者完成数。回调另外发起的异步工作沿 `Schedule` 的返回句柄和资源声明
保持其数据使用期，调用方按该完成句柄回收异步工作使用的数据。

嵌套调用复用当前 Scheduler 和 scope，并核对传入访问受当前权限覆盖。每位参与者都进入
并行回调上下文，数据接口继续区分单工作项写入能力。嵌套范围内发起的异步子工作挂到已有
scope，由该 scope 的完成句柄保持和交付；同步调用返回覆盖本次范围参与者退出。

ECS 的 `ReadOnlyQueryPacketJobs.ExecutePrepared` 将其借用的范围数组交给该同步入口，
外围查询保持 ECS 列的借用期，输出数组的外部访问交给同步资源入口。同步返回后，查询方
读取完整结果并归还范围数组。

本节属于已实现的公共接口与使用方法。源码入口：
[同步范围](../../../src/SomeEngine.Job/Scheduler.SynchronousParallel.cs)、
[共享队列](../../../src/SomeEngine.Job/WorkQueue.cs)、
[只读查询分区](../../../src/SomeEngine.ECS.Systems/ReadOnlyQueryPacketJobs.cs)。
公共行为检查：[同步结果和完成点](../../../tests/SomeEngine.Job.Tests/SynchronousParallelTests.cs)、
[只读查询结果](../../../tests/SomeEngine.ECS.Systems.Tests/ReadOnlyQueryPacketJobTests.cs)。
完整调用与预热后分配的执行入口见
[Job benchmark](../../../tools/SomeEngine.Job.Benchmarks/README.md)。

## Related concepts

完成分派在状态锁内从依赖记录取得独立的回调数组，后续分派持有该数组直到全部回调完成。工作完成、作用域完成和外部完成回调使用相同的数据持有边界；记录归还与复用只维护记录自身的数据。公共回归同时验证故障向所有依赖方传播，以及观察故障后其他任务继续完成。

- [[Job-Work-Released|WorkReleased]]
- [[Job-Scope-Completed|ScopeCompleted]]

## 工作类别与配额

`Scheduler.CreateCategory(JobCategoryConfig)` 创建属于该 Scheduler 的工作类别，
`JobScheduleOptions.Category` 随提交选择类别。`MaxInFlight` 限制在途根请求，
`MaxExecuting` 限制实际执行的队列 drainer 和同步调用线程，`ServiceShare` 指定类别之间
轮转取得就绪票的份额。每个类别内部继续按 `Priority` 选择就绪票；普通工作作为份额为 1
的默认类别参与轮转。

同步提交在在途额度用满时抛出 `InvalidOperationException`。`ScheduleAsync` 与
`ScheduleParallelAsync` 返回 `ValueTask<JobHandle>`，取得额度后交付实际工作的完成句柄。
等待期间，既有 Job 记录保持提交作用域、显式依赖和资源保留；取消令牌取消尚在等待额度的提交。
工作及其挂接子工作全部结束后，完成回调将额度交给下一位等待者。失败和 Scheduler 关闭
沿相同完成路径结束请求。同类别子工作复用其根请求额度，类别执行额度覆盖实际回调。

下面是使用示例，`job` 和 `access` 由调用方提供：

```csharp
JobCategory category = scheduler.CreateCategory(new JobCategoryConfig
{
    Name = "decode", MaxInFlight = 32, MaxExecuting = 2, ServiceShare = 1
});
var options = new JobScheduleOptions { Category = category, Priority = JobPriority.Low };
JobHandle handle = await scheduler.ScheduleAsync(job, access, options, cancellationToken: token);
await handle;
```

`ExecuteParallel` 同样接受该类别，调用线程参与执行并计入执行额度；同类别嵌套范围复用当前
执行许可。类别受限的并行提交按执行上限发布参与者，所有参与者继续使用共享就绪队列。

本节属于已实现的公共接口与行为。源码入口：
[类别配置](../../../src/SomeEngine.Job/JobCategoryConfig.cs)、
[类别等待与提交](../../../src/SomeEngine.Job/Scheduler.Categories.cs)、
[类别服务份额](../../../src/SomeEngine.Job/WorkQueue.Categories.cs)。
公共行为检查：[工作类别](../../../tests/SomeEngine.Job.Tests/JobCategoryTests.cs)。

并行提交的完成记录同时计入实际范围批次和参与 token 的退出。每个 token 先释放其批次组
引用，再归还退出计数；最后一位退出者归还类型化批次存储，随后既有完成记录交付结果。
因此 `Complete` 返回覆盖回调数据清理和批次归还。队列容量及执行统计继续按实际范围批次
更新。源码入口：[工作流与批次](../../../src/SomeEngine.Job/JobWorkStreams.cs)、
[执行完成](../../../src/SomeEngine.Job/Scheduler.Execution.cs)；公共检查为
[回调数据使用期](../../../tests/SomeEngine.Job.Tests/ResourceLifetimeTests.cs)。

## 自动批次与统计

本节属于已实现的算法与公共配置。Scheduler 按工作类型持有独立的自动批次代价，
每个类型按两倍工作规模区间分别保存单位元素时间的移动平均值。选批以
`max(1, WorkerCount)` 作为密度基数。每个样本以四分之一
权重更新已有代价。冷启动在配置密度上限内采用每 worker 最多四块；学习后的批大小
按 `AutoBatchTargetMicroseconds` 选择，并保持在冷启动密度与
`AutoBatchMaxTilesPerWorker` 之间。显式批大小沿用调用方指定值。

异步范围按每次领取量子汇总实际回调代价，同步范围按每位参与者汇总，调用线程也参与
同步采样。两种入口读写所属 Scheduler 的同一类型与规模代价。批次返回时清空该代价引用，
类型化批次池继续复用。

`EnableCounters` 控制诊断计数及就绪票时延采样。关闭时，自动批次保留其选批所需的
回调代价反馈；显式批大小直接执行。开启时，还更新线程计数与就绪票时间。
同步调用的 `ScheduledJobs`、`ExecutedWorkItems` 和 `FaultedWorkItems` 包含调用线程
实际完成的范围；`CompletedHandles` 对应实际完成记录。`QueuedWorkItems` 对应各入口
占用的队列工作容量，同步入口按入队参与者计数。

源码入口：[自动批次代价](../../../src/SomeEngine.Job/ParallelBatchSizer.cs)、
[同步范围](../../../src/SomeEngine.Job/Scheduler.SynchronousParallel.cs)、
[统计](../../../src/SomeEngine.Job/RuntimeCounters.cs)。公共检查：
[类型、规模与统计配置](../../../tests/SomeEngine.Job.Tests/AutomaticBatchTests.cs)；
完整调用测量：[Job benchmark](../../../tools/SomeEngine.Job.Benchmarks/README.md)。
