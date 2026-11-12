# 活跃范围中的数据访问

调用方通过 `Query.OpenRead()` 或 `Query.Open()` 建立查询范围；`QueryScope` 持有结构读取期。范围内枚举原生 `QueryChunk`，通过 `Read<T>()`、`Write<T>()`、`ReadBuffer<T>(row)`、`WriteBuffer<T>(row)` 取得具体列与缓冲。筛选直接形成连续匹配行段，chunk 内行号对应当前段。

随机访问通过 `World.OpenRead<T>()`、`OpenWrite<T>()` 建立类型范围。`ReadLookup<T>` 返回只读引用，`WriteLookup<T>` 返回可写引用并维护写版本。缓冲通过 `ReadBuffer<T>` 与 `GetBuffer<T>` 建立自己的读取或写入范围。层级遍历通过 `ChildrenRead` 持有读取期。

引用、span、buffer 与 chunk 由所在范围保证存储寿命；结构修改在范围结束后执行。需要跨阶段保存成员时，调用方显式复制 Entity 数组；需要保存 World 内容时使用存档 Capture。

接入 Job 的 World 由 `WorldJobs` 声明实际组件、结构及资源区间。生成 Job 在已获准的工作项中绑定列，范围动作采用具体 struct。调用链为：查询描述 → 访问登记与依赖 → QueryScope/工作项 → QueryChunk 列 → 具体算法 → 释放范围。

源码：[查询范围](../../../src/SomeEngine.ECS/Queries/Query.cs)、[类型读取与写入](../../../src/SomeEngine.ECS/Queries/ReadLookup.cs)、[执行连接](../../../src/SomeEngine.ECS.Systems/WorldJobs.cs)。可存储描述见 [Storable Descriptions](Storable-Descriptions.md)。
