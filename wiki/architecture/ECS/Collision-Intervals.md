# SAP 区间索引

本页记录已实现的 SAP、分区 SAP 算法与公共使用入口。源码为 [区间索引](../../../src/SomeEngine.Physics/CollisionIntervalIndex.cs)、[算法设置](../../../src/SomeEngine.Physics/CollisionIntervalSettings.cs)、[候选查询](../../../src/SomeEngine.Physics/CollisionIntervalCandidates.cs) 及网格共用的 [具体配对语义](../../../src/SomeEngine.Physics/CollisionPairing.cs)。公共候选与检测消费行为位于 [IntervalCandidateTests](../../../tests/SomeEngine.Physics.Tests/IntervalCandidateTests.cs)。

## 项目选择与实体输入

`Collision.CreateSap(axis)` 返回 `CollisionIntervalIndex<SapSettings>`。项目通过 `CollisionAxis.X`、`Y` 或 `Z` 选择排序和扫描轴。

`Collision.CreatePartitionedSap(axis, partitionSize)` 返回 `CollisionIntervalIndex<PartitionedSapSettings>`，同时选择轴向分区宽度。分区编号为所选轴坐标除以宽度后向下取整。

两种索引提供相同的 `Candidates` 重载，接收既有 `world.Within(members)`、`world.Between(first,second)` 或 `world.Product(first,second)`。参与语义沿原 [关系查询](Relation-Queries.md) 保持，网格与区间索引共同使用具体 `ICollisionPairing` 实现。

默认读取实体上的 `CollisionBounds`。`Candidates<TGeometry,TBoundsReader>(pairs,reader)` 在有效查询范围中调用具体 `ICollisionBoundsReader<TGeometry>`，从当前原组件派生包围范围。原参与查询继续持有各自的筛选条件，访问并集提供几何与规则所需组件读取。

## 共享排序与扫描

`CollisionIntervalIndex<TSettings>` 集中持有每个参与实体的派生范围、所选轴下界和上界、覆盖分区范围及本轮访问标记。实体到区间位置的字典提供跨批次更新入口；真实几何和运动数据继续由原组件持有。

候选准备按当前参与集合更新区间，移除本轮缺席实体，按轴向下界排序，并在下界相同时按实体身份排序。排序后更新实体到区间的位置映射。区间列表和实体字典持续复用容量。

普通 SAP 将所有区间放入同一分区。分区 SAP 把实体索引加入其范围覆盖的每个轴向分区。两种配置使用同一排序、分区组织和扫描实现。每个分区按全局排序顺序加入实体索引，因此分区内仍保持下界次序。

每个发起区间只向后扫描；后续下界超过其上界时结束该次扫描，再以完整 `CollisionBounds.Overlaps` 判断三轴范围相交。通过范围判断后，使用当前参与查询及所选具体配对语义输出实体对。

## 跨区输出与类型归并

跨区实体在覆盖的分区中保留索引。双方首分区编号的最大值确定其首个共有分区，由该分区输出这一对实体。位置积的自身配对由该实体首分区输出，再由原参与集合判断其位置角色。

分区保存实体区间索引，区间范围由索引本体持有。每轮准备清空并重填分区索引列表；空分区归还所属索引的复用池，后续分区继续使用这些列表容量。

排序后的分区成员条目形成连续范围，`IntervalPairRange(Start,Count)` 表示一段发起条目。范围可以跨越多个实际分区，数量由批次预留的工作分区容量决定。唯一输出分区规则使各工作范围产生互不交叠的配对输出。

`CollisionPairCandidates<TSource,TPartition,TFilter>` 保留这一具体分区类型，继续组合组件、键与位图的 `Where`、`Except`。关系数据从当前原组件行读取，变化随下一批次生效。`CollisionPairBins<IntervalPairRange>` 使用已有 count、prefix、write，把工作范围直接归并到最终类型区间；具体检测读取当前原组件，入口见 [碰撞类型分桶](Collision-Pair-Bins.md)。

## 作业与有效范围

同步枚举在原关系查询的组件借用范围内完成准备和扫描。并行分桶复用现有查询范围作业，在 count 与 write 返回前完成对应范围。

外层 Job 将 `index.GetWriteAccess(scheduler)`、批次 `FillAccess` 和查询组件读取加入实际访问声明。索引写访问通过已有实体位置字典的容器资源身份覆盖所属区间和分区更新。生产、检测与后续消费沿现有 JobHandle 接续，批次存储按其既有生命周期持有。
