# Dependencies

## Definition
依赖是计算读取时登记的 `(来源, 版本)`。每帧在视图重建阶段比较版本，变化的计算才重新执行。

| 来源 | 登记方式 | 失效条件 |
|---|---|---|
| ECS 组件 | `Read<T>(entity)`；版本由 `World.WriteVersion<T>` / `SparseWriteVersion<T>` 读取 | 该实体该组件的写版本变化（见 [[architecture/ECS/Change-Tracking|Change Tracking]]） |
| 实体结构 | `Has<T>(entity)` | 组件被添加或移除 |
| 查询成员 | `Each(query)` | 结果集的实体增删或顺序变化，与组件值无关 |
| 环境数据 | `ReadUp<T>()` | 找到的祖先组件变化，或树结构变化后重新查找 |
| 对象 | `Depend(cell)`，或读取 `State<T>.Value` | `VersionCell.Version` 变化 |

## Invariants
- 只有带版本的数据能被依赖。外部数据通过持有 `VersionCell` 接入，不必进入 ECS。
- 写入只推进版本，不推送通知；跨线程写入无需派发，下一次重建阶段即可看到。
- ECS 依赖存于节点的 `DynamicBuffer<Dependency>`；对象依赖存为 `(VersionCell 引用, 版本)` 数组，均不装箱。
- 按组件类型的全局写版本未变时，该类型的所有依赖整批跳过。
- 依赖粒度是整个组件。需要更细的粒度时按变化频率拆分组件（例如点击计数 `Clicks` 与悬停状态 `Hover` 分开），不提供字段级依赖。
- 列表的父计算只依赖 `Each` 的成员，每行是读取自身实体的子视图。

## ECS 组件类型写版本

`World.ComponentWriteVersion<T>() where T : struct` 返回当前 World 中表格或 sparse 组件类型的写计数。首次写入之前为 `0`；对任意实体添加、整值替换、取得可写引用或移除该类型时，每个写入口推进一次。连续同帧写入各自推进，对其它类型或其它 World 的写入各自维护自己的版本。

取得包含数据的可写 span 时，按这次借用推进一次；其中的全部值共用该次借用。空 span 与只读访问保留当前计数。生成 Job 每取得一个可写行引用推进一次，原子递增使完成后的计数在不同的 packet 执行顺序下保持一致。

`World.WriteBytes` 的整组件与范围重载沿用可写引用的计数，`QueryChunkView.WriteColumn` 沿用可写 span 的计数。这些入口同时记录实体行版本与组件类型计数；两种观察由各自公开 API 提供。联合行为验收见 [组件类型写版本测试](../../../tests/SomeEngine.ECS.Tests/ComponentTypeWriteVersionTests.cs)。

类型写版本以 `uint` 循环计数，调用方用相等比较判断变化。系统 tick 继续用于现有行版本、chunk 版本和系统观察窗口。类型计数使整批依赖检查能观察连续同帧写入。

表格类型计数由 World 的结构数据持有，sparse 类型计数由其唯一的 `SparseSet<T>` 持有。各存储在统一写入口维护计数；实体复制、销毁与命令回放沿相同入口更新。结构事务从当前计数建立候选数据，成功时随组件数据一起提交，回滚时保留原有数据与计数。

类型版本读取与已有类型的推进均为 O(1)。表格首次登记类型时扩展按组件编号索引的版本表；后续写入只定位该类型并原子递增。sparse 存储直接递增自身的类型计数。

## Query membership

查询成员依赖通过公开 ECS 接口读取：

```csharp
public partial class World
{
    public uint MembershipVersion(QueryHandle query);
}
```

- 每次读取观察查询当前遍历顺序中的实体序列。创建、销毁，以及组件增删引起的匹配变化推进版本；交换删除等操作引起的结果顺序变化同样推进版本。
- `Enabled<T>` 与 `Disabled<T>` 参与成员匹配；它们选择的实体进入或离开结果时推进版本。组件值写入，以及保持实体序列的结构与启用修改，保留已有成员版本。
- `Added<T>`、`Changed<T>` 与 `ChunkChanged<T>` 的时间窗口由查询执行提供。成员观察使用这些条件的组件存在要求，时间窗口仍由 `ExecuteQuery` 处理。
- 版本是查询观察的 `uint`，使用 `!=` 比较以覆盖回绕。多次修改在下一次读取时按当前结果合并观察。
- 相同查询定义的多次取得共用同一份成员观察。结构事务携带这份查询数据提交，失败回滚恢复原版本与原遍历顺序。
- 成员观察按需建立。静态读取先比较 chunk 身份、实体行序版本与查询过滤掩码，变化后才比较实际实体序列。结果范围内保持顺序的表格迁移保留版本。
- 调用方使用当前 World 的有效查询句柄；开发构建通过统一 `DevelopmentCheck` 报告无效或已释放的句柄。shipping 调用中的开发检查及其参数求值由条件编译移除。

实现：[World 查询 API](../../../src/SomeEngine.ECS/World.Queries.cs)、[成员观察](../../../src/SomeEngine.ECS/Queries/QueryMembership.cs)、[公开行为测试](../../../tests/SomeEngine.ECS.Tests/QueryMembershipTests.cs)。

## Relations
[[architecture/someui/State|State]] · [[architecture/someui/Rebuild-Schedule|Rebuild Schedule]] · ADR 0010
