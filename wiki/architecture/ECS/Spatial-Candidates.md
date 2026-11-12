# 空间候选

`SomeEngine.ECS.Relationships` 的 `SomeEngine.ECS.Spatial` 命名空间持有空间范围、二叉 BVH、网格与配对入口。`SpatialBounds` 保存世界坐标下的 Min、Max；两个范围在三个轴上均相交时产生候选，接触面相等也计为相交。物理的接触计算与预测范围继续由物理持有。

## 输入与读取范围

`SpatialQueries.Candidates(world, members)` 读取查询中的 `SpatialBounds`，返回具体 `RelationQuery<TreeCandidates<SpatialBounds,SpatialBoundsReader>,(Entity,Entity)>`。业务组件内持有几何时，使用具体 struct `ISpatialBoundsReader<TComponent>` 和 `Candidates<TComponent,TBoundsReader>`。

终端依据 `AddReads` 把几何及筛选组件加入可选读取并集，在同一 `RelationQueryViews` 中打开实际成员 `QueryScope`。构建按 `QueryChunk.TryRead<TComponent>` 取得列，再按行的组件存在性读取范围与完整 Entity。原成员查询的 Required、Stored、启用、排除与变化条件继续决定参与集合。

`SpatialQueries.Build(world,members)` 建立调用方持有的 `BoundsTree`；泛型重载使用业务范围读取器。把该树传入 `SpatialQueries.Candidates(world,members,tree)` 后，候选遍历直接读取同一棵树，并以本次范围的 `QueryScope.Contains(entity)` 筛选叶子。`tree.Rebuild<TComponent,TBoundsReader>(world,members,reader)` 在同一对象重用容量更新输入。

## 树与候选顺序

`BoundsTree` 集中持有节点数组与 Entity 到叶子的映射。节点保存范围、完整身份、父节点、两个子节点、子树成员数和高度。初建按中心轴分割，分支范围由子节点合并；维护使用同一节点编号更新范围及附加摘要。`Nodes` 在构建或维护完成后提供只读节点范围。

遍历从 `(root,root)` 开始：同一分支展开两个子树内部与两个子树之间的组合；不同分支先检查范围相交，再按树高细分。叶子输出按完整实体身份排序，每个无序实体对产生一次。过滤链在绑定的具体列与行号上执行；组级证明、类别和键摘要见 [空间筛选](Spatial-Filtering.md)。

```csharp
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;

using var world = new World();
world.Create(new SpatialBounds(Vector3.Zero, Vector3.One));
world.Create(new SpatialBounds(new Vector3(0.5f), new Vector3(1.5f)));
Query members = world.Query<SpatialBounds>();
BoundsTree tree = SpatialQueries.Build(world, members);
int pairCount = SpatialQueries.Candidates(world, members, tree).Count();
```

`pairCount` 为 1。完整的业务关系继续组合 `Where`、`Except` 与具体输出；调用链为成员查询 → 统一读取范围 → 树或网格候选 → 具体列筛选 → 身份对输出。

## 网格与增量维护

`SpatialQueries.CreateUniformGrid(cellSize)` 与 `CreateHierarchicalGrid(cellSize,levelCount)` 返回具体设置的 `SpatialGrid<TSettings>`。网格通过 `BeginUpdate`、`Update(entity,bounds)`、`EndUpdate` 维护当前成员与占用单元。`grid.Candidates(world.Within(query))` 从本次真实输入更新后枚举；`grid.ReadCandidates(world.Within(query))` 枚举调用方已更新的同一网格。Between 与 Product 的成员角色沿各自配对语义保留。

`SpatialMaintenance` 观察 `World.WatchMembership(query)`，结合组件行写版本更新同一 BVH 与摘要。维护及读取的 Job 依赖和访问声明见 [空间维护](Spatial-Maintenance.md)。并行枚举使用原生分区的 count/prefix/write 与 `ExecuteRanges`，见 [空间并行查询](Spatial-Parallel-Queries.md)。

源码：[树入口](../../../src/SomeEngine.ECS.Relationships/Spatial/TreeCandidates.cs)、[BVH](../../../src/SomeEngine.ECS.Relationships/Spatial/BoundsTree.cs)、[网格](../../../src/SomeEngine.ECS.Relationships/Spatial/SpatialGrid.cs)。公开行为由 `SomeEngine.Physics.Tests` 中的空间候选、筛选、维护与网格用例，以及 `SomeEngine.Navigation.Tests` 的实际邻居消费者覆盖。
