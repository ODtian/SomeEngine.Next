# 碰撞树维护

`CollisionMaintenance<TGeometry,TBoundsReader,TFilter,TGroupResults>` 根据 World 的成员事实和组件写入版本，维护调用方持有的 `CollisionTree` 及完整条件链的具体摘要。查询描述继续采用普通 struct，维护对象持有观察者、成功读取的版本基线和调度资源。

## 数据与更新顺序

`CollisionTree` 持有一个节点数组、完整 Entity 到叶子的对应关系和空闲节点链。初建完成中心轴排序后建立叶子对应。后续插入沿子范围增长量选择兄弟，连接新分支；移除将兄弟接到祖父并回收叶子及父节点；范围写入标记受影响祖先，再按子节点就绪顺序分层合并 Bounds、MemberCount 和 Height。节点地址复用让具体摘要继续按同一编号附加。

每次维护在作业实际执行时读取 `EntityTableChanges.Read()`，然后进入覆盖全部读取的 Access 回调和原参与集合回调：

1. 按批次身份核对当前成员及几何组件，并集中保存成员变更与需要读取范围的实体身份。重复身份在本轮合并，变化索引与输出 Bounds 容量由维护对象复用。
2. 用 `ChangedChunks(batch)` 合并成员变化和读取列的变更范围。`QueryChunkView.ReadRowChanges<T>()` 在 chunk 内绑定组件写入版本跨度与上次成功基线；`QueryRowChanges.HasChanged(row)` 读取原列中的行版本。几何先绑定一次，具体 `TFilter.ReadChanges<TGeometry,TExecution>` 在同一回调内提供闭合的摘要变化读取器。组合条件以具体类型组合读取器，几何与摘要读取同一种组件时共享几何变化事实。维护遍历原 `RowIndices` 并合并几何和摘要变化。
3. `ReadOnlyQueryPairJobs.ExecuteParallel` 在仍然有效的 Access/成员回调内按保存的实体身份读取原组件，具体 BoundsReader 直接消费原列引用并生成派生 Bounds。所有叶范围读取完成后，集中更新、插入或移除叶子，并去重标记受影响的祖先。
4. 按脏子节点数量组织 `RefitLayers`。每层 `BoundsRefitJob` 完成空间范围合并后，由具体 `TFilter.RefitSummary` 更新位摘要、键摘要，再推进到父层。`SpatialAnd` 与 `SpatialExcept` 直接转发到保留的具体条件。
5. 回调成功后 Accept 该批次，并把读取范围的 CurrentSystemVersion 保存为下一次 LastSystemVersion。

原位范围读取与分层更新位于 [CollisionMaintenance.Bounds](../../../src/SomeEngine.Physics/CollisionMaintenance.Bounds.cs)；[CollisionMaintenanceTests](../../../tests/SomeEngine.Physics.Tests/CollisionMaintenanceTests.cs) 从公开候选结果验证范围读取完成、成员变化与摘要更新。

首个观察批次包含当前存活身份，因此首次维护同时填充具体摘要。后续查询的 Enumerate、Count、Any 读取已更新数组。原始组件由 World 持有，树持有拓扑，每个具体摘要持有自己的派生数组，维护对象持有观察窗口。

实体创建、销毁、表迁移以及enable mask的实际变化都在原写入口进入同一 `EntityTableChanges` 身份批次。启用状态变化由 `Chunk.WriteEnabled` 返回已改变事实，组件写入口据此记录实体；组件值的行写版本继续表达原有值写入。维护按当前成员条件核对这些身份，因此Enabled和Disabled条件中的成员进入、离开沿同一成员变更收集和分层更新流程维护叶子。EntityStore持有批次及其版本，观察、记录、读取和接受共用同步；结构事务复制自己的观察事实，提交和回滚沿原数据根进行。

`QueryRowChanges`、组合变化读取器和执行回调都是短生命周期借用值，版本数组继续由原 chunk 持有。组件存在性、读取权限和 chunk 版本在绑定处取得，逐行检查使用原行编号与 `VersionClock.IsNewer`。`ISpatialChangeExecution.Execute<TChanges>` 保留具体读取器类型，读取作用域覆盖组合条件和维护行循环。

维护继续沿用当前系统时钟。写查询在访问激活后取得本次写版本；直接 World 整值写入使用当前系统版本，调用方在新系统窗口开始时调用 `AcquireSystemVersion()`。维护和只读查询的组件声明保持读取模式，成功读取的版本作为维护基线。

## Job 访问与依赖

`SomeEngine.ECS.Systems.QueryJobAccess.Count/CopyAccesses` 把实际 QueryDefinition 的现有规范化存储声明映射到 World Job 资源，包含 topology 和原访问模式。表格组件沿已有 ComponentInfo 的 alias 证明及类型分类取得能力。此映射供维护、读取以及同布局手写调用共同使用。

维护对象在创建时保存这一访问集合。`Schedule(dependency)` 声明组件、拓扑读取及索引 token 写入；`ScheduleRead(job,additionalAccesses,dependency)` 声明相同组件读取、索引 token 读取和调用方输出访问。`AccessCount`、`CopyReadAccesses` 与 `ReadAccess` 供其它具体调度入口使用。ADR 0002 规定显式依赖完成后的访问激活，帧的新鲜度通过传入的 JobHandle 连接。

以下是完整规则的业务例子，Body、Hair、运动规则和接触缓冲属于业务夹具：

```csharp
var tree = Collision.Build<Body, BodyBoundsReader>(world, bodies, default);
var contacts = Collision.Candidates<Body, BodyBoundsReader>(world, bodies, tree, default)
    .Where(default(DefaultContacts))
    .Where(default(MutualCategories), default(MutualCategoryBitmap))
    .Except(default(SameCharacterHair), default(HairCharacterIndex));
using var maintenance = contacts.CreateMaintenance(scheduler);
using var results = new SpatialContactBuffer(scheduler, pairCapacity, entityCapacity);

JobHandle updated = maintenance.Schedule(componentWriter);
JobHandle consumed = SpatialCollisionFrames.Schedule(maintenance, results, updated);
consumed.Complete();
ReadOnlySpan<(Entity, Entity)> pairs = results.Pairs;
```

`maintenance.Query` 保存复用树的具体查询描述。每帧沿该描述及同一维护对象继续调度。`SpatialCollisionFrames` 的读取作业先枚举身份对，再从当前 Body.Bounds 计算各接触的穿透深度，并累加双方的接触次数和深度。接触缓冲持有实际 Scheduler 创建的 token，输出访问与消费数据在同一持有处声明。调用方在读取及消费完成后释放维护对象、缓冲、查询及 World。

## 手写对照与行为依据

`benchmarks/SomeEngine.ECS.Benchmarks/Relations/HandwrittenSpatialMaintenance.cs` 持有自己的观察者、树、类别摘要、角色键摘要和版本。它使用同一节点结构操作及 QueryJobAccess，直接展开批次核对、Body/Hair 版本判断、父链和具体摘要 refit。查询进入独立的 `HandwrittenSpatialCollision.Filtered` 遍历。两侧消费同一个 `SpatialCollisionConsumption`，各自保存输出和接触结果。

`CollisionMaintenanceTests` 通过真实 checked World 和公开查询观察连续位置、类别和角色键写入；成员插入、移除、标签迁移与销毁；带显式同步点的写入、维护、接触消费依赖。对应检查核对字面身份对、完整重建结果、接触次数和穿透深度。World、QueryRow和QueryChunkView的启用写入分别通过直接动作检查，Enabled/Disabled两种输入均观察空→字面(A,B)→空的成员变化。

空间条件和组证明见 [空间筛选](Spatial-Filtering.md)，来源与初建见 [空间候选](Spatial-Candidates.md)。

计数、前缀和填充共享读取范围，输出 token 按实际区间声明，以及单线程／并行与手写帧调用见 [空间关系分区执行](Spatial-Parallel-Queries.md)。
