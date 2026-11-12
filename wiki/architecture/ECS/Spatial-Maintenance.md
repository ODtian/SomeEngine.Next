# 空间索引维护

[SpatialMaintenance<TGeometry,TBoundsReader,TFilter,TGroupResults>](../../../src/SomeEngine.ECS.Relationships/Spatial/SpatialMaintenance.cs) 位于 Relationships，命名空间为 `SomeEngine.ECS.Spatial`。它依据查询成员日志与组件写版本，更新一份 `BoundsTree` 和条件链的派生摘要。World 持有组件和成员事实，树持有空间拓扑，各具体摘要持有自己的节点数组，维护对象持有读取进度、成功版本和索引资源。

## 树与成员的维护

[BoundsTree](../../../src/SomeEngine.ECS.Relationships/Spatial/BoundsTree.cs) 保存节点数组、完整 Entity 到叶子的映射和空闲链。初建按中心轴排序并建立叶子映射；插入沿范围增长量选择兄弟，移除把兄弟连接到祖父并回收叶子与父节点，范围变化沿父链合并 Bounds、MemberCount 和 Height。节点编号复用，各摘要按同一编号附加数据。

维护对象创建时调用 `World.WatchMembership(query)`。读取者从登记后的序列开始；首次执行通过原生 `Query.OpenRead()` 的当前成员建立树和全部摘要。这个初始遍历直接读取已有成员。后续执行处理 `MembershipChange(Entity,Kind,Sequence)`，每个读取者独立 `Capture`、`Open` 与 `Advance`。

一次成功维护按以下顺序完成：

1. 调度依赖完成后捕获日志区间，取得其 `Ready` 和 `Read` 资源声明。
2. 在 `_access.OpenRead()` 与原成员 `QueryScope` 中读取该区间。对每个进入或离开的身份，按当前 `members.Contains(entity)` 及几何组件存在性核对；当前成员更新或插入叶子，其余身份移除叶子。
3. 在当前成员的 `QueryChunk` 中逐行检查几何的 `WasWritten<TGeometry>(row,LastSystemVersion)`，并结合具体筛选的 `HasChanges<TGeometry>`。需要观察写入的几何与摘要组件声明 `ITrackedComponent`。
4. 从变化叶子或移除后的最低分支向上更新。每一层先 `Tree.Refit(node)`，再 `Filter.RefitSummary(node,world)`，保持孩子先于父节点。
5. 读取范围结束后推进日志到已捕获的 `Through`，并保存本次读取前取得的 World 版本作为下一次基线。

结构入口发布真实查询进出；Required、Stored、Disabled、排除等条件继续由原生查询判定。启用掩码带来的集合变化进入同一日志。值写入保持原 chunk 的行版本，查询成员版本表达实体集合变化。普通命令回放的成功前缀发布对应日志；显式原子流程以 Preserve 候选 World 承接修改，交接时一起交付成员进度，见 [Change-Tracking](Change-Tracking.md)。

## 访问与完成链

`WorldExecution.CollectReadAccess(query)` 收集实际结构与组件读取；`WorldJobs` 校验同一类型存储分类并将声明映射到 Scheduler 资源。维护每次从当前读取描述取得这些声明。`BeginMaintenance` 在其依赖完成后捕获成员日志，并安排 `UpdateIndex` 子工作；父工作的完成点包含该子工作的索引更新。

`Schedule(dependency)` 串接上次维护及调用方依赖。更新工作声明组件读取、捕获的日志区间读取与索引 token 写入。`ScheduleRead(job,additionalAccesses,dependency)` 复用同一组件读取，加上索引 token 读取和业务输出声明。`AccessCount`、`CopyReadAccesses` 与 `ReadAccess` 供其它具体调度调用同一资源集合。帧的新鲜度由显式 `JobHandle` 依赖表达，资源冲突沿 Scheduler 规则排序。

```csharp
using SomeEngine.ECS.Spatial;

var tree = SpatialQueries.Build<Body, BodyBoundsReader>(world, bodies, default);
var contacts = SpatialQueries.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
    .Where(default(DefaultContacts))
    .Where(default(MutualCategories), default(MutualCategoryBitmap))
    .Except(default(SameCharacterHair), default(HairCharacterIndex));
using var maintenance = contacts.CreateMaintenance(scheduler);
using var pairs = new SpatialPairBuffer<BoundsNodePair>(scheduler, partitionCapacity: 16);
JobHandle updated = maintenance.Schedule(componentWriter);
// 调用方的普通 IJob 在其 Execute 中运行 maintenance.Query.FillPairsParallel(pairs)。
JobHandle filled = maintenance.ScheduleRead(fillPairsJob, [pairs.FillAccess], updated);
filled.Complete();
ReadOnlySpan<(Entity, Entity)> rows = pairs.Pairs;
```

Body、Hair、规则与接触累计属于业务代码。消费者从完成后的实体对及真实组件计算接触次数和穿透深度；树与摘要仍由同一个维护对象更新。`Dispose` 完成已有维护、关闭成员读取者并释放索引资源。

## 均匀网格

[SpatialGrid<TSettings>](../../../src/SomeEngine.ECS.Relationships/Spatial/SpatialGrid.cs) 使用同一实体与 `SpatialBounds` 数据。`BeginUpdate`、`Update(entity,bounds)`、`EndUpdate` 维护成员、占用单元和移出的身份；单元列表与空列表缓存由网格本身复用。`grid.Candidates(world.Within(query))` 在准备时从真实查询范围更新，`grid.ReadCandidates(world.Within(query))` 读取调用方已经更新的同一网格。

Navigation 的 [NavigationNeighborSystem](../../../src/SomeEngine.Navigation/NavigationNeighborSystem.cs) 从实际反馈样本计算边界并更新网格，再经 Within 候选筛选半径、层、掩码和最近邻。它与通用配对共用这份网格。

公开行为位于 [CollisionMaintenanceTests](../../../tests/SomeEngine.Physics.Tests/CollisionMaintenanceTests.cs)、[GridCandidatesTests](../../../tests/SomeEngine.Physics.Tests/GridCandidatesTests.cs) 与 Navigation.Tests。组摘要与方向见 [空间筛选](Spatial-Filtering.md)，count/prefix/write 的范围见 [空间并行查询](Spatial-Parallel-Queries.md)。
