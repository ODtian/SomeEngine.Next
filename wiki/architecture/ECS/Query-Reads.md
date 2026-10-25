# 声明资源的查询读取

本页记录当前公共 API 和读取职责。源码入口为 [World.ReadQueries](../../../src/SomeEngine.ECS/World.ReadQueries.cs)、[World 访问声明](../../../src/SomeEngine.ECS/World.JobAdmission.cs)、[WorldJobs](../../../src/SomeEngine.ECS.Systems/WorldJobs.cs) 和 [QueryReadResources](../../../src/SomeEngine.ECS.Systems/QueryReadResources.cs)。实际消费行为由 [PhysicsExtractionTests](../../../tests/SomeEngine.Render.Tests/PhysicsExtractionTests.cs) 核对。

## 列与拓扑

`World.ExecuteReadQuery` 在调用线程执行只读查询回调，支持系统版本基线和调用方的 `ref` 状态。它持有拓扑读取及查询声明的组件列、Buffer、sparse 和 shared 访问，范围持续到回调返回。绑定 `WorldJobs` 时，这些访问进入现有 Scheduler 的实际资源前沿；未绑定 Job 的 World 持有原同步修改互斥范围。

直接回调中的 World 修改受只读约束。该约束关联进入回调时的 Job 执行深度；协作等待所推进的独立 Job 使用其自身资源权限。原查询视图和组件引用在所属回调中取得与使用。

## 引用数据

查询持有者在冷路径创建 `QueryReadResources`，用 `Add<TComponent,TResources>()` 注册已有 `IJobComponentResources<TComponent>` 具体描述器。查询定义同时声明相应组件读取。

在有效查询回调内，`resources.Execute(query, ref state, callback)` 按当前有效行调用描述器的 `Collect` 与 `GetVersion`，取得所指对象及数组范围的同步资源访问，再复核绑定版本。绑定版本变化时重新收集。访问覆盖整个消费回调，并在返回时归还；内部数值工作可继续复用现有同步范围入口。

描述器按原组件读取实际引用，版本列表和访问列表由所属 `QueryReadResources` 复用。持有者串行使用同一个读取描述，实际工作、等待及归还由现有 Job 资源机制承担。`GetVersion` 的含义沿用组件资源描述器的绑定变化规则，见 [资源访问与就绪](Readiness-Admission.md)。

## 渲染提取

`RenderExtractionQuery.ReadOptional<T,TResources>()` 同时登记组件读取和引用资源描述。提取的范围收集、`CompleteSourceCollection` 及资源值收集处于同一实际读取范围。具体入口与骨骼引用数据职责见 [Extraction](../rendering/Extraction.md)。
