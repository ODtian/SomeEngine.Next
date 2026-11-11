# 同步范围执行

公共同步执行使用 `Scheduler.ExecuteRanges<TWork,TItem>` 与具体 `IRangeWork<TItem>`。`ExecuteParallel` 负责确定资源访问与批大小，再进入同一范围执行内核。

调用方先声明真实资源区间和显式完成依赖。Scheduler 等待语义前驱、取得资源、执行具体范围并释放工作访问，随后完成范围内提交的后代。等待使用协同执行，零 worker 配置沿相同路径前进。

每个工作项绑定自己的范围。单工作项沿当前访问权限执行；多个范围执行时增加并行深度，随机写能力沿既有声明限制。嵌套范围保留外层并行权限，返回时恢复原执行状态。

ECS 通过 `WorldJobs` 连接访问声明与范围：查询描述 → 组件/结构资源区间 → Job 依赖与资源准许 → 原生 `QueryChunk` 或具体列动作 → 完成。同步查询可使用 `Query.ForEach<...,TAction>`，手动读取使用 `Query.OpenRead()`，手动写入使用 `Query.Open()`。

源码：[范围内核](../../../src/SomeEngine.Job/Scheduler.Ranges.cs)、[执行连接](../../../src/SomeEngine.ECS.Systems/WorldJobs.cs)、[原生查询](../../../src/SomeEngine.ECS/Queries/Query.cs)。
