# 变化追踪

## 系统观察窗口

每个系统以 `SystemState` 持有 `ulong LastSystemVersion` 与 `CurrentSystemVersion`。`SystemGroup<TContext>` 为本轮取得版本，依次调用创建、更新与收尾入口；成功后推进该系统的 LastSystemVersion。非泛型 `SystemGroup` 根据真实访问建立 Job 图，成功完成后推进对应状态。消费者通过 `GetState(index)` 或注册返回的状态读取同一观察窗口。

`World.AcquireSystemVersion()` 分配新版本；`World.Version` 读取当前版本。`World.OpenWriteBatch()` 为同一批实际写入共享惰性版本。`Query.Since(version)` 将观察窗口交给 Changed/Added 筛选，`QueryScope` 和生成 Job 在实际范围中使用它。空工作保留已有值版本。

## 原生组件事实

`ITrackedComponent` 声明 Written 追踪；Added/Removed 能力沿类型声明形成。`QueryChunk.WasWritten<T>(row, since)` 读取行版本，`WasWritten<T>(since)` 读取列摘要。`WasAdded<T>(row, since)` 读取添加事实。`World.ComponentDataWriteVersion<T>(entity)` 同时覆盖表与 sparse；`ComponentWriteVersion(componentId)` 读取类型写版本。版本以 `ulong` 保存和比较。

`Removed<T>` 在原生 cleanup 列保存旧值和移除版本。消费者通过普通查询读取后，使用 `ClearRemoved<T>(through)` 清理已处理事实。Destroyed 与 cleanup 数据共同维持清理所需的实体身份。

## 查询成员日志

`Query.MembershipVersion` 表示成员集合的最近变化。`World.WatchMembership(query)` 返回 `EventReader<MembershipChange>`，每条包含 Entity、Added/Removed 与 Sequence。读取者从登记时的尾部开始观察。

World 在结构编辑前后依据原生查询匹配事实判断成员进出。每个表缓存实际登记查询；成员进出按批写入对应日志。可启用组件的筛选变化沿相同路径形成事实，普通 chunk 行顺序变化继续使用当前捕获视图。

读取者声明 `Read`、依赖 `Ready`，调用 `Open()` 取得固定 Through，完成消费后 `Advance(through)`。各读取者独立推进，日志按最慢读取者回收；扩容保留全部待消费内容并计入资源，停滞由统一 validation 报告。

普通 `CommandBuffer.Playback` 保留成功前缀及其成员事实。整批原子操作显式在候选 World 执行，成功后通过 `WorldCheckpoint.Restore` 交接到原 World，保留已有查询与读取者身份。失败时源 World 继续持有此前值、版本与日志。

## 销毁与消费者

`World.WatchDestroyed()` 使用同一事件日志机制。Views 在自己的消费阶段声明 Computations 的 sparse 访问，再释放关联计算；渲染提取、空间维护分别观察自己的成员查询。通用交付规则由 [ADR 0016](../../../docs/adr/0016-events-and-step-results.md) 持有。

源码：[系统状态与图](../../../src/SomeEngine.ECS.Systems/SystemGroup.cs)、[生命周期](../../../src/SomeEngine.ECS.Systems/SystemGroup.Lifecycle.cs)、[查询](../../../src/SomeEngine.ECS/Queries/Query.cs)、[行版本](../../../src/SomeEngine.ECS/Queries/QueryChunk.Values.cs)、[成员日志](../../../src/SomeEngine.ECS/Changes/World.Membership.cs)。
