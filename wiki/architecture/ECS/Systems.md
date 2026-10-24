# System  [[architecture/ECS-Architecture|ECS Architecture]]


system callbacks 按显式 order 和 registration tie-break 在 coordinator thread 上顺序调用。callback 负责生命周期、读取轻量配置、提交 jobs，并可调用显式 `Execute` 进入 caller owner；它不能在 owner外取得 ECS ref/span。job bodies 按真实 resource/range 并行。

`SystemGroup` 的提交观察者由当前线程持有可复用栈。每个生命周期回调进入作用域时压入自己的观察者，退出时按嵌套顺序移除；提交仍登记到各层作用域与资源关联的生命周期。正常结束与异常退出沿同一清理边界恢复栈。容量预热后，持续帧更新的作用域进入和退出使用已有存储。

每份接受的 Job 使用登记在提交期间与完成回调期间分别保持引用，完成或回滚只结束一次；两个引用释放后，登记对象进入共享池。完成分派借用数组池中的回调数组，调用完毕后清除引用并归还。具体并行工作组使用带锁的连续栈复用组对象。数据和回收入口见 [JobSubmissionLifetime](../../../src/SomeEngine.Job/JobSubmissionLifetime.cs)、[Scheduler.ExecutionStore](../../../src/SomeEngine.Job/Scheduler.ExecutionStore.cs) 与 [JobWorkStreams](../../../src/SomeEngine.Job/JobWorkStreams.cs)。

`ISystem<TContext>` 作为公开系统扩展入口。注册系统时，具体 `TSystem` 为更新建立 managed 函数指针；`SystemGroup` 每次系统更新通过该入口调用具体系统。`OnCreate`、`OnDestroy` 继续沿生命周期扩展分发。每系统上下文、提交作用域和成功回调后的版本写回沿现有 `SystemSlot` 维护。

函数地址保存在完整 `SystemFunctions<TSystem, TContext>` 类型的共享静态表中，注册时写入节点。节点持有实际系统值，更新入口按该完整类型取得系统并调用 `OnUpdate`。热帧使用节点已经确定的地址，实例状态继续由各自节点持有。具体 struct 系统、class 系统的共享泛型与接口调用形状，连同驱动、查询和字段路径的实测成本，见 [类型化动画执行证据](../../evidence/Animation/2026-10-16/Typed-Execution.md)。

公开 `World.ExecuteQuery` 同时接受 managed 函数指针 `delegate* managed<QueryCursor, void>` 和 `delegate* managed<QueryCursor, ref TState, void>`。有状态形式支持 `allows ref struct`，调用方通过 `ref` 持有上下文。查询在回调前取得声明的数据访问和版本，回调成功后提交关系变化，异常时沿共同边界处理关系恢复。批内组件值、字段访问和值运算保留具体类型，间接分发位于查询回调边界。

动态扩展在声明阶段可用 `QueryDefinitionBuilder.WithAccess(inputs)` 合入其公开输入定义。输入的组件及缓冲访问按原读写权限作为 `Optional` 条目合并，实体匹配由当前查询的必需项和过滤条件决定。实际执行复用共同的存储权限、缓冲访问展开与版本处理；图条件等扩展据此登记各具体状态和输入能力。

system order 只表达 callback/lifecycle 顺序，不暗示数据 freshness。需要 B 读取 A 的新结果时，B 的 job显式依赖 A 的 handle；紧密 job pipeline 应在同一 feature/system 中组合，generated compound pipeline 可自动传递精确 handle。

删除以下正确性机制：

- `SystemContext.GlobalDependency`；
- frame-start reset 和 frame-end `Complete`；
- `SystemAccessManifest`；
- `SystemScheduleBuilder` 的 read/write stages；
- `RequiresBarrierAfter`、public barrier 和 `CompleteConflicts`。

disable 阻止未来 update submission。remove 先关闭 system Lifetime Domain，等待该 system 已提交 roots 的 `ScopeCompleted`，然后调用 `OnDestroy`。World/runtime 使用相同 close protocol。

