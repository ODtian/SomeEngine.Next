# 系统

`SystemGroup<TContext>` 管理一个具体上下文的系统集合。`Add<TSystem>` 保存具体系统及更新入口并返回注册编号，`GetState(index)` 返回对应 `SystemState`。更新按注册顺序调用；创建、启用、移除和关闭沿同一生命周期管理。每个状态持有 `ulong` Last/CurrentSystemVersion，成功完成更新后推进观察窗口。

非泛型 `SystemGroup(world, scheduler)` 为具体 `IComponentAction`、chunk 动作与普通 `IJob` 建立执行图。`Add` 和 `AddJob` 返回状态，`After(system, predecessor)` 表达明确的执行顺序，`Update` 返回本轮完成点。图由实际 `SystemAccesses` 推导资源依赖；结构变化后重建图。

数据访问通过原生 Query、QueryScope、QueryChunk、类型 lookup 与 `SystemChunk` 进行。生成 Job 的资源声明由生成器读取，含引用组件通过具体 `CollectAdditionalResources` 收集真正使用的资源。`SystemChunk.HasSparse<T>`、`ReadSparse<T>` 与 `GetSparse<T>` 沿同一工作项读取和写入 sparse 数据。

系统回调提交异步工作时，消费者依赖实际 JobHandle。系统自身的提交寿命覆盖其工作和后代；移除或关闭在工作完成后执行销毁入口。结果交付使用 [事件与步结果](../../../docs/adr/0016-events-and-step-results.md)。

调用链：系统注册 → 状态和访问声明 → 顺序图/Job 资源登记 → 具体范围动作 → 成功完成 → 系统版本推进。版本语义见 [Change Tracking](Change-Tracking.md)。

源码：[系统图](../../../src/SomeEngine.ECS.Systems/SystemGroup.cs)、[生命周期](../../../src/SomeEngine.ECS.Systems/SystemGroup.Lifecycle.cs)、[chunk 入口](../../../src/SomeEngine.ECS.Systems/ChunkActions.cs)、[Job 连接](../../../src/SomeEngine.ECS.Systems/WorldJobs.cs)。
