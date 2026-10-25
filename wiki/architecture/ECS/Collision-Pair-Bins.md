# 碰撞类型分桶

本页记录现有公共 API 与数据职责。实现入口为 [候选分区](../../../src/SomeEngine.Physics/CollisionPartitions.cs)、[类型批次](../../../src/SomeEngine.Physics/CollisionPairBins.cs) 和 [查询终端](../../../src/SomeEngine.Physics/CollisionPairBinQueries.cs)；公开行为入口为 [CollisionPairBinTests](../../../tests/SomeEngine.Physics.Tests/CollisionPairBinTests.cs)。

## 候选来源

`ICollisionPartitionEnumerable<TPartition>` 在既有 `IQueryRelationEnumerable<(Entity,Entity)>` 查询声明上增加三个行为：

- `Prepare(queries)` 在本次借用范围内更新候选所需派生数据。
- `CreatePartitions(partitions)` 写入互不交叠的具体配对分区。
- `Enumerate(partition, queries, ref output)` 枚举该分区中的实体对，继续执行来源组合的关系规则。

BVH 的 `ICollisionPartitionEnumerable` 使用 `CollisionNodePair`，由自身 `CollisionTree` 创建节点对分区。树节点和树引用由 BVH 来源持有。

普通 `Within`、`Between`、`Product` 查询在组合 `Where`、`Except` 后调用 `PartitionPairs()`，以 `RelationPairPartition` 表达完整关系的一份配对范围。原来源继续负责集合成员、无序去重、位置顺序及自身配对语义。其他候选来源通过自己的 `TPartition` 进入相同分桶终端。

## count、prefix、write

项目创建 `CollisionPairBins<TPartition>(scheduler, partitionCapacity, binCount)`，显式提供分区容量与检测类型数。`ICollisionPairClassifier.AddReads` 将分类所需组件加入既有访问声明；`Classify(first,second)` 从原组件行返回 `CollisionPairType(Bin,FirstGeometry,SecondGeometry)`。两个几何编号由项目的具体几何表示解释。

`FillPairBins` 与 `FillPairBinsParallel` 执行相同三个阶段，也提供系统版本基线重载：

1. count 按候选分区枚举关系，累积“分区 × 检测类型”的输出数量。
2. prefix 按检测类型、分区顺序生成写入位置，扩展最终输出容量。
3. write 在同次有效查询视图下重新枚举关系，直接填入最终类型范围。

`CollisionGeometryPair` 保存双方实体及双方几何编号。`ReadBin(bin)` 返回该检测类型的连续只读范围。分区、计数、写入位置、输出容量由同一个类型批次复用；几何值继续由实体组件持有。

并行终端通过 [ReadOnlyQueryTripleJobs](../../../src/SomeEngine.ECS.Systems/ReadOnlyQueryTripleJobs.cs) 借用完整访问视图及两个参与集合视图，再调用所属 World 的 `Scheduler.ExecuteParallel`。每轮同步范围在外层查询借用结束前完成。单集合来源在两个参与位置复用同一成员视图。

## 检测与完成依赖

`query.DetectPairs(bins, bin, detection)` 以具体 `TDetection : ICollisionPairDetection` 执行一个类型范围。检测的 `AddReads` 声明几何读取；`Execute(pair,first,second)` 收到实体/几何身份及当前原组件行。项目通过具体检测类型提供接触计算与输出持有者。

`query.DetectPairsWithQuery` 以具体 `ICollisionPairQueryDetection` 取得独立的完整访问视图，使几何读取器能够沿实体引用读取参与集合之外的模拟节点。共同几何、接触输出和独立查询见[实体几何](../physics/Geometry.md)。

外层 Job 将批次的 `FillAccess` 加入分桶工作访问，将 `ReadAccess` 加入检测或其他消费工作访问，并用现有 `JobHandle` 串联生产与消费。count 和 write 的内部范围按实际计数与输出区间声明；组件查询作用域覆盖对应同步计算。`Dispose` 在所属工作完成后释放批次的资源身份。
