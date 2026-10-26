# 声明资源的查询读取

本页记录当前公共 API 和读取职责。源码入口为 [World.ReadQueries](../../../src/SomeEngine.ECS/World.ReadQueries.cs)、[World 访问声明](../../../src/SomeEngine.ECS/World.JobAdmission.cs)、[WorldJobs](../../../src/SomeEngine.ECS.Systems/WorldJobs.cs) 和 [QueryReadResources](../../../src/SomeEngine.ECS.Systems/QueryReadResources.cs)。实际消费行为由 [PhysicsExtractionTests](../../../tests/SomeEngine.Render.Tests/PhysicsExtractionTests.cs) 核对。

## 列与拓扑

`World.ExecuteReadQuery` 在调用线程执行只读查询回调，支持系统版本基线和调用方的 `ref` 状态。它持有拓扑读取及查询声明的组件列、Buffer、sparse 和 shared 访问，范围持续到回调返回。绑定 `WorldJobs` 时，这些访问进入现有 Scheduler 的实际资源前沿；未绑定 Job 的 World 持有原同步修改互斥范围。

直接回调中的 World 修改受只读约束。该约束关联进入回调时的 Job 执行深度；协作等待所推进的独立 Job 使用其自身资源权限。原查询视图和组件引用在所属回调中取得与使用。

已借用的 `QueryRow.WriteVersion<T>()` 读取原组件行的当前写版本。
`row.ReadBuffer<T>().WriteVersions` 从同一 [BufferView](../../../src/SomeEngine.ECS/BufferView.cs)
返回 Header、Inline 两列各自的行写版本；Buffer 内容变化继续由原写入路径标记。
版本数据只在原借用内读取，输入持有者可保存它们用于判断后续原源数据的变化。

## 引用数据

查询持有者在冷路径创建 `QueryReadResources`，用 `Add<TComponent,TResources>()` 注册已有 `IJobComponentResources<TComponent>` 具体描述器。查询定义同时声明相应组件读取。

在有效查询回调内，`resources.Execute(query, ref state, callback)` 按当前有效行调用描述器的 `Collect` 与 `GetVersion`，取得所指对象及数组范围的同步资源访问，再复核绑定版本。绑定版本变化时重新收集。访问覆盖整个消费回调，并在返回时归还；内部数值工作可继续复用现有同步范围入口。

`ExecuteEntities(query, entities, ref state, callback)` 使用调用方给出的实际参与实体范围，
按原查询的成员、行过滤和组件读取规则取得这些实体引用的数据。完整查询持有列访问，
参与实体范围决定本次引用资源；两种入口共用资源等待、绑定复核和回调归还流程。
该入口同时接受实现 `IQueryReadEntities.Count/GetEntity` 的具体泛型投影，并支持 `ref struct`。
碰撞 pair 等只读连续数据可直接投影参与实体，Span 重载沿同一投影流程执行。

描述器按原组件读取实际引用，版本列表和访问列表由所属 `QueryReadResources` 复用。持有者串行使用同一个读取描述，实际工作、等待及归还由现有 Job 资源机制承担。`GetVersion` 的含义沿用组件资源描述器的绑定变化规则，见 [资源访问与就绪](Readiness-Admission.md)。

## 引用资源的异步工作

`ScheduleEntities(world, query, entities, in job, accesses, dependency)` 接受可保存的
`IQueryReadEntities` 具体投影和具体 `IJob`。先等待显式输入依赖，再在查询读取范围中
收集本次参与实体的原组件引用及实际对象、数组访问。实际工作取得列与引用数据访问后
重开读取范围，复核描述器版本和实际访问集合；绑定变化时沿同一个 Job 子工作链重新收集，
消费 Job 在有效绑定上执行一次。返回句柄覆盖收集、重试、消费和消费产生的子工作。
源码入口为 [QueryReadResources.Schedule](../../../src/SomeEngine.ECS.Systems/QueryReadResources.Schedule.cs)，
公开使用见 [QueryReadResourceTests](../../../tests/SomeEngine.ECS.Systems.Tests/QueryReadResourceTests.cs)。

收集与执行的每个查询视图都在所属回调内取得和使用。资源描述、版本列表、实际引用和工作容量
由本次请求独立持有，完成后归还读取持有者复用。实际对象及数组访问沿现有
`JobResourceAccessBuilder`、组件资源使用记录与 Scheduler 就绪规则执行。

`AddFrom(resources)` 在冷路径合并具体描述器。带资源参数的
`QueryJobAccess.CopyAccesses(world, definition, destination, resources)` 映射这些描述器
覆盖的托管组件只读列；其余列继续采用原 alias-free 声明规则。查询本身仍沿
`World.ExecuteReadQuery` 的只读访问规则执行。

## 渲染提取

`RenderExtractionQuery.ReadOptional<T,TResources>()` 同时登记组件读取和引用资源描述。提取的范围收集、`CompleteSourceCollection` 及资源值收集处于同一实际读取范围。具体入口与骨骼引用数据职责见 [Extraction](../rendering/Extraction.md)。
