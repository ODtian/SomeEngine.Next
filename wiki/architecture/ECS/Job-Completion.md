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


## Related concepts

完成分派在状态锁内从依赖记录取得独立的回调数组，后续分派持有该数组直到全部回调完成。工作完成、作用域完成和外部完成回调使用相同的数据持有边界；记录归还与复用只维护记录自身的数据。公共回归同时验证故障向所有依赖方传播，以及观察故障后其他任务继续完成。

- [[Job-Work-Released|WorkReleased]]
- [[Job-Scope-Completed|ScopeCompleted]]
