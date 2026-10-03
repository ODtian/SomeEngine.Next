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

## ECS 读取依赖实现（#9）

```csharp
public static class Scope
{
    public static ref readonly T Read<T>(Entity entity) where T : struct;
}

public struct Dependency : IBufferElement
{
    public Entity Entity;
    public ushort Component;
    public uint Version;
}
```

`Scope.Read<T>` 从当前 World 的表格或 sparse 存储返回组件的只读引用。
`Rerun` 构建按 `(Entity, Component)` 登记读到的实体组件写版本；同一次构建重复读取该来源时，
保留首次读取的版本。一个实体上的不同组件，以及不同实体上的同一组件，分别形成依赖。

视图实体首次登记时添加 `DynamicBuffer<Dependency>`。每次构建开始先清空已有缓冲，
随后按该次构建实际走过的读取路径重新登记。嵌套子构建使用自己的作用域；
子构建返回后，后续读取继续登记到父视图。`Once` 构建沿相同存储路径读取，作用域保持仅取值状态。

重建阶段先按下述组件类型检查筛选来源，再读取这些来源的实体组件写版本，以 `!=` 比较登记版本。
表格行与 sparse dense 行继续持有各自版本，视图依赖缓冲保存该次读取的观察值。
源组件与版本通过 ECS 既有只读访问权限取得。调度顺序与候选执行前的复核见
[[architecture/someui/Rebuild-Schedule|Rebuild Schedule]]。

调用方在当前计算所在 World 中使用有效实体及其已有组件，并在可能移动数据的结构修改前消费返回的引用。
作用域和组件使用错误沿统一的 `DevelopmentCheck` 开发检查报告。

实现：[Scope](../../../src/SomeEngine.Views/Scope.cs)、[依赖数据](../../../src/SomeEngine.Views/Dependency.cs)、
[ECS 读取桥接](../../../src/SomeEngine.ECS/World.ComponentData.cs)。
公开验收：[ReadDependencyTests](../../../tests/SomeEngine.Views.Tests/ReadDependencyTests.cs)，
覆盖组件更新、父子共享来源、释放子树、读取来源切换、混合存储重复读取及嵌套构建。

## ECS 组件类型写版本

`World.ComponentWriteVersion<T>() where T : struct` 返回当前 World 中表格或 sparse 组件类型的写计数。首次写入之前为 `0`；对任意实体添加、整值替换、取得可写引用或移除该类型时，每个写入口推进一次。连续同帧写入各自推进，对其它类型或其它 World 的写入各自维护自己的版本。

取得包含数据的可写 span 时，按这次借用推进一次；其中的全部值共用该次借用。空 span 与只读访问保留当前计数。生成 Job 每取得一个可写行引用推进一次，原子递增使完成后的计数在不同的 packet 执行顺序下保持一致。

`World.WriteBytes` 的整组件与范围重载沿用可写引用的计数，`QueryChunkView.WriteColumn` 沿用可写 span 的计数。这些入口同时记录实体行版本与组件类型计数；两种观察由各自公开 API 提供。联合行为验收见 [组件类型写版本测试](../../../tests/SomeEngine.ECS.Tests/ComponentTypeWriteVersionTests.cs)。

类型写版本以 `uint` 循环计数，调用方用相等比较判断变化。系统 tick 继续用于现有行版本、chunk 版本和系统观察窗口。类型计数使整批依赖检查能观察连续同帧写入。

表格类型计数由 World 的结构数据持有，sparse 类型计数由其唯一的 `SparseSet<T>` 持有。各存储在统一写入口维护计数；实体复制、销毁与命令回放沿相同入口更新。结构事务从当前计数建立候选数据，成功时随组件数据一起提交，回滚时保留原有数据与计数。

类型版本读取与已有类型的推进均为 O(1)。表格首次登记类型时扩展按组件编号索引的版本表；后续写入只定位该类型并原子递增。sparse 存储直接递增自身的类型计数。

## 按组件类型检查依赖（#11）

每个 World 的 `ViewRebuildSystem` 保存自己已经检查过的依赖组件类型与各类型上次帧开始的写计数。
每帧开始，各类型读取一次当前 `ComponentWriteVersion`，以 `!=` 判断该类型是否变化。
收集候选时，计数相等的类型直接跳过其依赖的实体组件版本读取；计数变化的类型继续逐条比较
依赖登记的版本。一个类型的其它实体被写入时，同样进入逐条比较，由来源实体的版本决定视图是否变化。

首次遇到某依赖类型时，先完成该类型的逐条比较，再让下一帧使用类型计数筛选。
这覆盖第一次构建登记依赖后、同一帧后续构建又写入该来源的情况。
系统只保存按类型编号索引的版本数据与类型列表，依赖来源继续由节点的 `DynamicBuffer<Dependency>` 持有。

候选执行前再次检查参数与依赖。对于帧开始计数相等的类型，复核当前类型写计数；
父构建或此前的候选构建写入该类型时，继续比较来源实体的当前组件版本。
帧开始记录的计数保留到下一帧，用来观察候选收集之后产生的写入。
新依赖类型、不同 World 和表格 / sparse 组件沿相同的检查规则处理。

实现：[ViewRebuildSystem](../../../src/SomeEngine.Views/ViewRebuildSystem.cs)。
公开行为验收继续使用 [ReadDependencyTests](../../../tests/SomeEngine.Views.Tests/ReadDependencyTests.cs)，
覆盖不同类型分别变化、父构建后的同帧写入、第一次子构建后的写入与各 World 的独立显示结果。
本地检查耗时见 [[architecture/someui/Performance|Performance]]。

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
