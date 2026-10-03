# Change tracking  [[architecture/ECS-Architecture|ECS Architecture]]

## 系统级观察基线

当前系统更新由 `SystemGroup<TContext>` 管理。每个已注册系统通过 `SystemSlot` 保存自己的 `LastSystemVersion` 和 `CurrentSystemVersion`，上下文从该系统的这两个版本建立。

一次启用系统的更新按下列顺序运行：

1. driver 取得本次 `CurrentSystemVersion`。
2. 建立上下文，执行 `BeforeUpdate`，首次运行时执行 `OnCreate`。
3. 执行 `OnUpdate` 和 `AfterUpdate`。
4. 将本次 `CurrentSystemVersion` 写回该系统的 `LastSystemVersion`。

`LastSystemVersion` 表示该系统上一次成功完成上述回调顺序的版本基线。同一系统内的查询消费者从系统上下文取得这条基线；不同系统分别保存各自的基线。回调抛出异常时保留上一次成功写回的版本，下一次更新继续从该基线观察变化。

异步 Job 的数据顺序通过显式 `JobHandle` 依赖和资源访问规则表达。系统回调的版本写回与具体 Job 的完成事件分别按各自执行路径判断。

## 时钟窗口与查询写入版本

`AcquireSystemTick()` 推进 World 时钟并返回推进前的值；`AcquireSystemVersion()` 返回推进后的新值。`ImmediateSystemDriver` 使用后者建立本次系统版本。

公开 `World.ExecuteQuery(query, lastSystemVersion, callback)` 在取得查询访问后建立当前执行版本：可写查询取得新版本，只读查询读取 World 当前 tick。调用者提供观察基线，World 根据查询声明选择本次版本。

`ExecuteQuery(query, callback)` 的可写形式同时取得推进前基线和推进后版本；只读形式以当前 tick 同时建立两个值。查询行和 chunk 中的写入使用当前执行版本发布变更。

`QueryRow.Read<T>()` 返回组件值；`Write<T>()` 和 `ReadWrite<T>()` 返回组件引用。查询对象及回调边界从当前 `QueryCursor`、`QueryRow` 和 `QueryChunkView` API 读取。

## Job 与拓扑写入版本

generated `IJobEntity` 在取得访问权限、完成 chunk/row 过滤并即将进入声明 World 写入的执行行时，惰性取得该次执行共享的版本。串行路径由 `GetExecutionVersion()` 保存版本，并行路径让匹配 packets 使用同一个共享版本对象。

hierarchy propagation 在实际 `Write` 的关系、组件和访问检查通过后取得共享版本，并将其用于组件替换。该版本由 `HierarchyPropagationExecutionVersion` 在第一次写入时建立。

topology packet 将 Parent 修改写入暂存数据。后续 `ParentFinalizerJob` 在验证当前 preimages 并进入结构写入候选后取得一次提交版本，再用该版本完成这一批 Parent/Children 变更发布。

## 决策与实现位置

用户在 2026-10-03 确定按当前系统级基线整理文档。此前查询级 `SystemChangeCheckpoints` 与异步 `QueryCheckpointHandle` 的描述属于更细粒度检查点的设计讨论；未来相关需求通过新的决策和验收要求记录。

源码：[SystemSlot](../../../src/SomeEngine.ECS.Systems/SystemSlot.cs)、[SystemGroup](../../../src/SomeEngine.ECS.Systems/SystemGroup.cs)、[ImmediateSystemDriver](../../../src/SomeEngine.ECS.Systems/ImmediateSystemDriver.cs)、[查询与 World 时钟](../../../src/SomeEngine.ECS/World.Queries.cs)、[QueryRow](../../../src/SomeEngine.ECS/Queries/QueryRow.cs)、[generated Job](../../../src/SomeEngine.ECS.Systems/JobEntityRuntime.cs)、[hierarchy propagation](../../../src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs)、[topology finalizer](../../../src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs)。

相关主题：[[Systems]] · [[Synchronous-Execute]] · [[Readiness-Admission]] · [[Hierarchy-Change-Tracking]] · [[Relation-Change-Tracking]] · [[architecture/ECS/Component-Bytes|Component Bytes]]
