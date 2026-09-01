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

- [[Job-Work-Released|WorkReleased]]
- [[Job-Scope-Completed|ScopeCompleted]]
