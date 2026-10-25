# 碰撞网格

本页记录已实现的网格算法与公共使用入口。源码为 [CollisionGrid](../../../src/SomeEngine.Physics/CollisionGrid.cs)、[网格配置](../../../src/SomeEngine.Physics/CollisionGridSettings.cs)、[候选查询](../../../src/SomeEngine.Physics/CollisionGridCandidates.cs)；公开行为通过 [GridCandidateTests](../../../tests/SomeEngine.Physics.Tests/GridCandidateTests.cs) 核对。

## 选择与参与集合

`Collision.CreateUniformGrid(cellSize)` 返回 `CollisionGrid<UniformGridSettings>`。所有成员按所选单元边长写入其范围覆盖的单元。

`Collision.CreateHierarchicalGrid(cellSize,levelCount)` 返回 `CollisionGrid<HierarchicalGridSettings>`。第 l 层的单元边长为 `cellSize × 2^l`。成员按包围范围最长边选择相应层级，层号受显式层数限制。

两种网格提供相同的 `Candidates` 重载，接收既有 `world.Within(bodies)`、`world.Between(first,second)` 或 `world.Product(first,second)`。集合内与集合间输出互不相同实体的无序对；位置积保留参与位置和自身配对。集合间交叠成员按相同无序语义去重。

默认形状输入为实体上的 `CollisionBounds`。`Candidates<TGeometry,TBoundsReader>(pairs,reader)` 通过具体 `ICollisionBoundsReader<TGeometry>` 从实体原组件读取范围。成员视图保留原查询筛选，完整访问视图持有所需组件读取；网格只保存派生 Bounds、实体身份及单元数据。

## 单元维护与候选枚举

每次候选准备按当前参与集合更新成员。成员保留其上次层级与单元范围；范围跨越单元或层级时，从旧单元移除并加入新单元。继续处于同组单元的成员更新派生 Bounds。准备结束时移除本轮集合中的缺席成员。实体列表、成员字典及单元字典持续复用容量；空单元的成员列表归还网格自己的复用池。

分层枚举让每个成员查询其自身层及更粗层。同层实体按完整实体身份选择一方负责枚举，跨层由更细层成员发起。双方覆盖的多个共享单元中，坐标逐项取两个最小单元坐标的最大值，确定唯一负责输出的单元。最终仍执行真实 Bounds 相交判断。

`GridPairRange` 表达一段发起成员范围。通用 [碰撞类型分桶](Collision-Pair-Bins.md) 按这些具体范围执行 count、prefix、write；各分区覆盖互不交叠的候选输出。

## 关系与批次

网格返回 `CollisionPairCandidates<TSource,TPartition,TFilter>`。该公共包装持有具体候选源、分区类型及具体筛选值。`Where`、`Except` 生成重载继续返回此包装，并组合 `CollisionPairAnd`、`CollisionPairExcept`。

`CollisionComponentFilter`、`CollisionKeyFilter`、`CollisionBitmapFilter` 分别调用既有组件关系、键关系与位图关系绑定，读取当前原组件行。筛选在所选候选来源的枚举中执行，规则变化随下一批次读取生效。公共包装同样供其他具体分区来源使用。源码为 [具体筛选](../../../src/SomeEngine.Physics/CollisionPairFilters.cs) 与 [生成入口](../../../src/SomeEngine.ECS.SourceGen/SpatialRelationGenerator.cs)。

异步使用时，外层 Job 将 `grid.UpdateAccess(scheduler)`、类型批次 `FillAccess` 及查询组件读取加入实际访问声明。网格访问使用已有成员字典的对象资源身份，覆盖所属成员与派生单元更新；生产、检测、求解和后续消费通过现有 JobHandle 接续。
