# 空间关系分区执行

`FillPairs(buffer)` 和 `FillPairsParallel(buffer)` 在一次关系查询读取范围中完成分区、计数、前缀与填充。两者接收同一个具体来源和筛选类型，输出由 `CollisionPairBuffer.Count/Pairs` 表示。带 `lastSystemVersion` 的重载将该基线交给原有成员条件，计数和填充使用同一个版本窗口。

## 来源与配对分区

`ICollisionPartitionEnumerable` 在 `IQueryRelationEnumerable<(Entity,Entity)>` 上增加 Tree、Prepare 和从 `CollisionNodePair` 开始的具体枚举。该空间族使用一个成员查询，QueryCount 为 1。`CollisionCandidates` 与 `SpatialCandidates` 实现这个接口，查询描述继续采用普通 struct。Prepare 在终端读取范围中运行一次：自动来源构建树及具体摘要，调用方持树的来源读取已维护的数据。

`CollisionTree.CreatePartitions` 从根自配对开始，优先细分成员配对数最大的工作。相同分支展开为左/左、左/右和右/右；不同节点对沿较高分支二分。各分区覆盖互不交叠的实体对，分区容量决定本次工作的数量上限。每个分区的正反逐条件证明从 Unknown 开始，再按原有范围相交、组证明、成员检查和叶子语义遍历。

## 一次读取中的阶段

`RelationQuery` 先进入 Access，再进入原参与集合。该范围持续覆盖以下全部阶段：

1. 为每个分区执行具体计数输出，保存实际结果数量。
2. 前缀作业建立连续 offsets，保存真实总数，并按需要扩展实体对数组。
3. 为每个有输出的分区取得其 offsets 区间，重新执行相同来源和筛选，写入对应实体对范围。
4. 等待全部填充作业完成，再退出原成员及 Access 回调。

`ReadOnlyQueryPairJobs` 是 Systems 提供的共同借用接点。`IReadOnlyQueryPairJob.Execute(index,first,second)` 在实际子任务中取得两个 QueryCursor。helper 的内部读取描述保存当前 World、QueryState 和 Last/CurrentSystemVersion；调用方仍在两个外层查询回调中持有原组件和 topology 读取。helper 在 finally 中合并并 Complete 已成功提交的全部工作，随后归还工作句柄数组。对外返回的是已完成的阶段 handle，前缀和填充继续通过该 handle 衔接。

因此每个分区访问原有组件存储，计数和填充共用同一有效输入。第三方具体来源及手写遍历也通过该接口进入相同读取和任务生命周期。

## 输出数据与资源区间

`CollisionPairBuffer` 持有节点对分区、counts、offsets、有输出的分区编号及实体对数组。buffer 创建的资源 token 属于传入的实际 Scheduler。外层调度通过 FillAccess 序列化同一缓冲的多阶段调用；各阶段按自己的数据声明访问：

| 阶段 | 读取声明 | 写入声明 |
| --- | --- | --- |
| 计数 | 外层借用的组件、成员及树 | count token 的 `[partition,1]` |
| 前缀 | count token | offset token，以及保存数量和数组容量的 pair token |
| 填充 | 外层借用的组件、成员及树，offset token 的 `[partition,2]` | pair token 的 `[offset,count]` |
| 接触消费 | 组件读取及 pair token | 业务接触累计数据 token |

每个计数或填充分区是独立 Scheduler job，填充 owner 取得自己的实际输出范围。Count/Pairs 只公开当前总数对应的输出。后续作业声明 `buffer.ReadAccess` 并显式依赖填充结果；下一帧组件写入依赖上一帧消费完成。

## 实际业务调用与手写对照

以下 Body/Hair 与接触累计属于业务例子，完整调用位于 `benchmarks/SomeEngine.ECS.Benchmarks/Relations/SpatialParallelFrames.cs`：

```csharp
using var pairs = new CollisionPairBuffer(scheduler, partitionCapacity: 16);
using var contacts = new SpatialContactTotals(scheduler, entityCapacity);
JobHandle updated = maintenance.Schedule(componentWriter);
JobHandle filled = SpatialCollisionFrames.SchedulePairsParallel(maintenance, pairs, updated);
JobHandle consumed = SpatialCollisionFrames.ScheduleConsume(maintenance, pairs, contacts, filled);
consumed.Complete();
ReadOnlySpan<(Entity, Entity)> rows = pairs.Pairs;
```

`SchedulePairs` 进入单线程 count/prefix/fill；`SchedulePairsParallel` 提交并行分区；`ScheduleParallel` 连接并行填充与消费。业务累计数据保存每个实体的接触次数和穿透深度。两种执行使用同一具体几何消费。

`HandwrittenSpatialPartitions` 通过相同接口调用独立的手写组遍历。手写维护对象公开具体 Query，帧夹具为它提供同名调度重载。两侧各持树、摘要、buffer 和累计数据，使用相同节点布局、分区、Scheduler、阶段资源区间和消费格式。持续帧两侧按相同成员和版本更新自己的索引。

`SpatialParallelTests` 的公开结果依据包括四个指定接触对、连续帧从五对到三对的四条执行链及接触累计，以及计数开始后排队组件写入的同步检查。最后一项先观察原三对结果，再维护写入后的树取得一个结果；带 Changed 成员条件和显式版本基线的填充取得对应成员窗口。

索引维护及其资源生命周期见 [碰撞树维护](Spatial-Maintenance.md)，组证明见 [空间筛选](Spatial-Filtering.md)。
