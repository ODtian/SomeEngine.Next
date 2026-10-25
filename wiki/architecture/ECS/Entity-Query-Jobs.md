# 按实体集合执行查询工作

本页记录已实现的实体范围调度与借用接口。源码入口为 [EntityQueryJobs](../../../src/SomeEngine.ECS.Systems/EntityQueryJobs.cs)、[受限查询视图](../../../src/SomeEngine.ECS.Systems/EntityQueryViews.cs)、[引用资源绑定](../../../src/SomeEngine.ECS.Systems/QueryReadResources.EntityQueries.cs)和 [QueryDefinition](../../../src/SomeEngine.ECS/Queries/QueryDefinition.cs)。公共行为入口为 [EntityQueryJobsTests](../../../tests/SomeEngine.ECS.Systems.Tests/EntityQueryJobsTests.cs)。

## 数据与访问声明

`IEntityQueryWork` 是项目提供的具体工作数据，声明每批参与的原查询与实体集合：

```csharp
int Count { get; }
int QueryCount { get; }
QueryHandle GetQuery(int workIndex, int queryIndex);
ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex);
```

原查询的组件读写模式决定访问，实体跨度决定实际范围。同一批可分别提供受作用实体的可写查询、邻居和固定输入的只读查询，以及各约束、参与边或其他元数据查询。查询筛选在取得当前行时继续生效。

`QueryDefinition.AsReadOnly()` 从原 `Terms` 保留成员、过滤与 Buffer 项，将实际写访问降为读取。项目在初始化时注册原查询及所需的只读派生查询，随后按阶段选择对应句柄；注册引用继续由调用方持有和释放。

`EntityQueryJobs.Schedule(world, in work, in job, accesses, resources, dependency)` 接收具体 struct 工作与算法、额外外部访问、可选 `QueryReadResources` 及前置完成依赖。`accesses` 可以声明派生图、索引或项目数据的实际资源访问。`resources` 复用原 `IJobComponentResources<T>` 描述器取得组件所引用对象及其范围。

## 有效视图与原位写入

算法实现 `IEntityQueryJob.Execute(int index, scoped EntityQueryViews queries)`。`queries.Get(i)` 返回该批的 `EntityQuery`：

| 接口 | 数据与行为 |
| --- | --- |
| `Entities` | 本批为该查询提供的实体引用 |
| `Rows` | 满足原筛选的当前 `QueryRow` |
| `TryGetRow` | 在本批实体集合和原查询筛选内解析当前行 |
| `TryReadRef<T>` | 从对应当前行直接借用可读组件 |
| `CurrentSystemVersion` | 本次调度共享的写入版本 |

行继续使用既有 `ReadRef`、`ReadWrite`、`ReadBuffer` 与 `Buffer` 等接口。算法在回调中直接访问当前组件及 Buffer；回调返回结束这些 ref/span 的借用。多个工作项的写入复用本次调度的版本，所需实体、行索引与资源声明容量通过内部工作区复用。

以下是项目组件处理的使用例子；`PointValue` 为该项目的组件，查询 0 声明实际写入实体：

```csharp
public readonly struct AddPoints : IEntityQueryJob
{
    public void Execute(int index, scoped EntityQueryViews queries)
    {
        foreach (QueryRow row in queries.Get(0).Rows)
            row.ReadWrite<PointValue>().Value += 1;
    }
}

JobHandle completed = EntityQueryJobs.Schedule(world, in work, new AddPoints(),
    graphAccesses, resources, dependency);
completed.Complete();
```

工作数据与其实体跨度保持有效至返回句柄完成。调用方在完成后修改派生集合、释放查询或结束相关实体输入的使用期。

## 调度、重绑定与完成

调度先完成显式依赖，再在 World 拓扑读取下解析当前行位置。表组件和 Buffer 复用生成式 Job 的 `StableQueryPacketAddress` 编码与 `WorldStorageJobResources` 键；Sparse 范围沿实体索引表达。连续地址归并为实际区间，chunk 变化过滤的读取沿已有整 chunk 规则声明。额外对象资源与这些声明一起进入原 Job 资源表。

每批以单个普通 `IJob` 执行其数值回调，各批的冲突由实际范围决定。写入前若结构代数或拓扑版本已推进，该批沿当前实体重新捕获行位置与范围，再登记接续工作；已完成批保留自身结果。这与已有组件引用调度的输入复核、重新捕获职责一致。

有引用资源时，原收集器按每个查询的实际实体读取当前组件引用，登记描述器给出的对象访问，随后复核配置版本及实际资源集合。引用变化接续重新收集；位置变化接续实体范围重绑定。源列、拓扑和实际对象访问覆盖具体算法的完整工作项，数值回调在有效借用内同步完成。

原返回句柄包含捕获、各批、重绑定、引用等待及算法附加的后代工作。全部工作完成后，内部查询与资源工作区回到复用池。关系端点结构修改继续沿现有命令与回放入口执行，图和着色决定算法的分组及次序，Job 访问声明负责实际数据冲突。

引用读取的一般规则见 [Query Reads](Query-Reads.md)，完成和就绪规则见 [Readiness](Readiness-Admission.md)。
