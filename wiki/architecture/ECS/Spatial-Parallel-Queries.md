# 空间关系分区执行

[ISpatialPartitionEnumerable<TPartition>](../../../src/SomeEngine.ECS.Relationships/Spatial/BoundsPartitions.cs) 在普通配对来源上增加 `Prepare(RelationQueryViews)`、`CreatePartitions(Span<TPartition>)` 和从指定分区枚举的入口。通用接口、BVH、均匀网格及输出缓冲均位于 Relationships 的 `SomeEngine.ECS.Spatial`。碰撞表面和预测等具体组织由 Physics 继续持有。

## 分区算法

树来源的 `TPartition` 为 `BoundsNodePair`。[BoundsTree.CreatePartitions](../../../src/SomeEngine.ECS.Relationships/Spatial/BoundsPartitions.cs) 从根自配对开始，选择配对数量最大的区域细分。同一分支展开为左/左、左/右、右/右；不同节点对沿较高分支二分。各分区覆盖互不交叠的无序实体对。拆分后按原顺序压缩确定为空的单叶自配对和范围分离节点对，`PartitionCount` 保存实际工作数量。

网格来源的 `TPartition` 为 `GridPairRange`，按成员区间划分。具体 `WithinSpatialPairing`、`BetweenSpatialPairing`、`ProductSpatialPairing` 保留原输入角色、自身组合和去重规则。两个后端通过同一泛型输出缓冲执行。

## 同一读取范围内的三个阶段

[SpatialPairBuffer<TPartition>](../../../src/SomeEngine.ECS.Relationships/Spatial/SpatialPairBuffer.cs) 的 `FillPairs`、`FillPairsParallel` 共用来源、条件和输出过程；对应 `ulong lastSystemVersion` 重载传入同一成员观察窗口。

1. `Prepare` 构建自动来源的索引和摘要，或读取调用方已维护的索引，然后产生分区。
2. 对每个分区执行具体计数输出，写入该分区的 count。
3. 全部计数返回后，协调者构造连续 offsets，保存真实总数，扩展实体对数组，并记录有输出的分区编号。
4. 对每个有输出的分区重放同一来源和筛选，写入 `[offset,count]` 的独立 Span。
5. 全部填充返回后退出本次关系查询读取范围。

并行阶段以 `IRangeWork<int>` 和分区编号数组进入 `Scheduler.ExecuteRanges`。调用方连接 `WorldJobs`，外层原生 `QueryScope` 提供当前结构与组件 grant；每个参与执行者在这个 grant 内打开自己的原生查询范围。计数与填充共用受保护的输入，协调者在同步 join 后读取 count、构建前缀，再进入填充。普通串行入口在同一线程执行相同的具体 work。

```mermaid
flowchart LR
    Q[QueryScope 与当前 grant] --> P[Prepare 与分区]
    P --> C[ExecuteRanges 计数]
    C --> O[协调者 offsets 与容量]
    O --> W[ExecuteRanges 独立输出区间]
    W --> R[Count 与 Pairs]
```

## 数据与资源

缓冲持有分区、counts、offsets、分区编号、有输出的分区编号和实体对数组。Scheduler 创建的一份 `_pairsResource` 保护整次填充及后续消费；counts 和 offsets 是该缓冲在这次调用内使用的数据。分区 work 的写入独立性来自 count/prefix 得到的真实数组区间。

| 阶段 | 实际读取 | 实际写入与授权 |
| --- | --- | --- |
| 外层填充 | 来源组件、成员、树或网格 | `pairs.FillAccess` 覆盖本次输出 |
| 并行计数 | 共享当前查询 grant | 各分区自己的 count 元素 |
| 协调者前缀 | 完成后的 counts | offsets、Count、容量 |
| 并行填充 | 同一输入与已完成 offsets | 各分区的 `[offset,count]` |
| 消费 | `pairs.ReadAccess` 与业务组件 | 调用方声明的累计数据 |

`Pairs` 只公开 `Count` 对应的范围，`FilledPartitionCount` 表达有结果的实际分区数。消费者声明 `ReadAccess` 并显式依赖填充完成；下一次填充通过同一输出资源与消费建立排序。

```csharp
using var pairs = new SpatialPairBuffer<BoundsNodePair>(scheduler, partitionCapacity: 16);
JobHandle maintained = maintenance.Schedule(componentWriter);
JobHandle filled = maintenance.ScheduleRead(fillPairsJob, [pairs.FillAccess], maintained);
JobHandle consumed = maintenance.ScheduleRead(consumePairsJob,
    [pairs.ReadAccess, contacts.WriteAccess], filled);
consumed.Complete();
ReadOnlySpan<(Entity, Entity)> rows = pairs.Pairs;
```

`fillPairsJob` 与 `consumePairsJob` 是业务的普通 struct `IJob`：前者调用 `maintenance.Query.FillPairsParallel(pairs)`，后者从实体对和实际组件计算接触累计。两者保持通用交付数据与业务状态各自的持有者。

公开检查位于 [SpatialParallelTests](../../../tests/SomeEngine.Physics.Tests/SpatialParallelTests.cs)，覆盖字面实体对、串行与并行一致性、持续帧结果及显式写入依赖。维护进度见 [空间维护](Spatial-Maintenance.md)，逐方向组证明见 [空间筛选](Spatial-Filtering.md)。
