# World Streaming

## Definition

`SomeEngine.Assets.WorldStream` 借用目标 World 与 AssetLoader，持有一次世界目录的请求、需求、读取、完成字节和已装入成员。`WorldStreamSystem` 持有同一 WorldStream，并通过 `ISystem<ImmediateSystemContext>` 接入 GameWorld.Systems。

## Data

| 数据 | 存储与公开读取 | 写入位置 |
| --- | --- | --- |
| Contents | WorldContent 数组，ReadOnlyMemory | 目录取得 |
| Requests | 条目到优先级的字典，Priorities | Request / Release |
| Demands | 完整 ContentDemand 数组，Changed 索引有效前缀 | 需求归并 Job |
| Reads | 条目到异步字节任务的内部字典 | Update |
| ReadResults | ContentBytes 列表，Items | Update 收取与提交 |
| LoadedGroups | 条目到 Entity[] 的字典，TryGetEntities | 整组提交与卸载 |

目录取得复用 WorldBlock.Contents 的同一底层数组。`ContentDemand` 包含 Required 与 Priority；`ContentBytes` 包含 ContentIndex 与 Bytes。

## CPU and I/O order

`StreamingSource` 的 Radius / Priority 与最近完成的 WorldTransform 构成空间来源。系统读取同一数据范围内的 WorldOrigin，将 float 局部位置转换为 double 绝对位置，写入 WorldStreamSource 数组。

Update 接受 `ReadOnlyMemory<WorldStreamSource>` 和 `JobHandle dependency`。查询 Job 读取来源与目录空间索引，写各来源独立的命中集合；归并 Job 读取命中与 Requests，写完整 Demands.Values 及 Required / Priority 发生变化的索引。最终需求取显式请求与空间覆盖的并集，优先级取所有有效来源的最大值。Job 声明实际数组、来源内存持有对象与集合的资源访问。Update 在消费需求前完成这条 CPU 链，调用方随后可复用借出的来源内存。

Update 按本轮需求卸载成员、收取完成字节、启动读取并选择提交。I/O 任务只交付字节；Update 持有可变流送表。收取时需求已经结束的字节释放本流送引用。`WorldSerializer.ReadEntities` 整组完成后，成员数组进入 LoadedGroups。

## Budgets

读取和提交都按当前需求优先级选择。MaxConcurrentReads 限制本流送的在途任务数量；已收取字节达到 MaxReadyBytes 时暂停启动新读取。CommitBudget 控制本轮开始装入的组数，已经开始的组完整完成。正预算先开始一组，随后按已用时间决定下一组；零预算保留完成字节供后续操作。默认值分别为 4、64 MiB 和 2 ms。

## Explicit startup completion

ContentsReady 是目录完成 Task。`CompleteReadsAsync()` 汇合调用时已经启动的读取任务，将字节任务的完成、取消与失败交给宿主；Update 继续承担结果收取与提交。

```csharp
await stream.ContentsReady;
stream.Request(0);
while (!stream.LoadedGroups.TryGetEntities(0, out _))
{
    stream.Update();
    await stream.CompleteReadsAsync();
}
```

Runtime 的初始场景显式请求全部目录项，沿这个完成点等待，再由既有 display.Wait 泵窗口。初始组就绪后，资源解析与每帧系统继续使用同一个 GameWorld 和 WorldStreamSystem。

## Lifetime

Release 清除显式请求，下一次 Update 结合空间来源计算需求。卸载筛选该组仍活跃的原生成员，调用 World.Destroy 批量销毁后移除 LoadedGroups 条目。UnloadAll 清空请求、当前需求与已装入组；后续 Update 根据新输入计算。

Dispose / DisposeAsync 取消并完成本流送的字节任务，卸载成员并释放本对象的数据引用。World 与 AssetLoader 由调用方持有。应用关停先完成 Game 工作，在既有 prepare 边界消费已有提取，再关闭流送、提取最终删除并消费到 Rendering World，随后释放系统、World 与资产设施。

## Relations

[世界目录与实体资产](Asset-Worlds.md) · [资产读取与生命周期](Asset-Loading-and-Lifetime.md) · [项目启动与渲染接入](Asset-Application-and-Rendering.md)

实现：[WorldStream](../../../src/SomeEngine.Assets/WorldStream.cs)、[需求归并](../../../src/SomeEngine.Assets/WorldStream.Demands.cs)、[WorldStreamSystem](../../../src/SomeEngine.Assets/WorldStreamSystem.cs)。公开行为：[流送检查](../../../tests/SomeEngine.Assets.Tests/WorldStreamTests.cs)。
