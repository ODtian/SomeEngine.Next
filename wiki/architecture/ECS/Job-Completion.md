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


## Related concepts

完成分派在状态锁内从依赖记录取得独立的回调数组，后续分派持有该数组直到全部回调完成。工作完成、作用域完成和外部完成回调使用相同的数据持有边界；记录归还与复用只维护记录自身的数据。公共回归同时验证故障向所有依赖方传播，以及观察故障后其他任务继续完成。

- [[Job-Work-Released|WorkReleased]]
- [[Job-Scope-Completed|ScopeCompleted]]
