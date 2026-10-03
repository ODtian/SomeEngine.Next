# System  [[architecture/ECS-Architecture|ECS Architecture]]


system callbacks 按显式 order 和 registration tie-break 在 coordinator thread 上顺序调用。callback 负责生命周期、读取轻量配置、提交 jobs，并可调用显式 `Execute` 进入 caller owner；它不能在 owner外取得 ECS ref/span。job bodies 按真实 resource/range 并行。

`SystemGroup` 的提交观察者由当前线程持有可复用栈。每个生命周期回调进入作用域时压入自己的观察者，退出时按嵌套顺序移除；提交仍登记到各层作用域与资源关联的生命周期。正常结束与异常退出沿同一清理边界恢复栈。容量预热后，持续帧更新的作用域进入和退出使用已有存储。

system order 只表达 callback/lifecycle 顺序，不暗示数据 freshness。需要 B 读取 A 的新结果时，B 的 job显式依赖 A 的 handle；紧密 job pipeline 应在同一 feature/system 中组合，generated compound pipeline 可自动传递精确 handle。

删除以下正确性机制：

- `SystemContext.GlobalDependency`；
- frame-start reset 和 frame-end `Complete`；
- `SystemAccessManifest`；
- `SystemScheduleBuilder` 的 read/write stages；
- `RequiresBarrierAfter`、public barrier 和 `CompleteConflicts`。

disable 阻止未来 update submission。remove 先关闭 system Lifetime Domain，等待该 system 已提交 roots 的 `ScopeCompleted`，然后调用 `OnDestroy`。World/runtime 使用相同 close protocol。

