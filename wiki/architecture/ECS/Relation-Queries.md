# Relation Queries

## 语义关系查询

配对查询位于 [SomeEngine.ECS.Relationships/Pairing](../../../src/SomeEngine.ECS.Relationships/Pairing/)，命名空间为 `SomeEngine.ECS.Relations`。`IRelation<T1,…,Tn>.Matches(in …)` 表达一至八个组件位置之间的业务语义。参与集合使用原生 `Query`；一元输出是 `Entity`，二至八元输出是实体 `ValueTuple`，第八个位置通过 `Item8` 读取。

`QueryDescription` 保存成员条件和读取声明，World 持有绑定后的查询数据。`Query.OpenRead()` 形成 `QueryScope`，枚举结果是原生 `QueryChunk`。成员条件包括必需组件、存储成员、Any、排除、可选读取、启用状态、共享值及 Added/Changed。关系终端在这些真实范围内读取成员和组件，范围结束时释放本次借用。

[RelationQuery](../../../src/SomeEngine.ECS.Relationships/Pairing/RelationQuery.cs) 保存具体来源值。`IQueryRelationEnumerable<TRow>` 的 `World`、`QueryCount`、`GetQuery(position)` 与 `AddReads(description,position)` 描述输入和访问；终端合并各输入定义中的实际读取类型，再加入规则所需的可选列。外层打开访问查询，随后逐位置打开原成员查询。`RelationQueryViews` 借用这些 `QueryScope`，`First` 至 `Eighth` 保留输入位置，`Access` 持有完整组件读取范围。

`Enumerate(ref output)`、`Count()`、`Any()` 共用这条执行链。对应 `ulong lastVersion` 重载把基线传给各参与 `Query.Since(version)`，保留成员过滤的观察窗口。只读执行保持 World 版本。终端通过一个活动 `QueryIdentityOutput` 持有输出值，最外层 `finally` 将它归还调用方；`IRelationOutput<TRow>.Add(in row)` 返回 true 继续、false 提前结束，支持持有 `Span` 的 ref struct 输出。

## chunk 列绑定

[IQueryRelation](../../../src/SomeEngine.ECS.Relationships/Pairing/IQueryRelationOutput.cs) 的具体读取协议为：

```csharp
void Bind(QueryChunk first, QueryChunk second);
bool Matches(in TRow row, int first, int second, bool reversed);
```

来源在进入一对 chunk 时绑定列，随后传递两个块内行号和身份换位事实。`IQueryRelationOutput` 使用相同的 `Bind` 与 `Add(in row,first,second,reversed)` 继续传递这些事实。`UsesBoundColumns` 区分需要列的筛选与仅消费身份的终端。

生成的 `ComponentRelation<TRelation,…>` 为一、二元绑定保存 `ReadOnlySpan<T>` 与对应 `QueryChunk`。列通过 `TryRead<T>` 取得；每行的可选存在性通过 `Has<T>(row)` 判断。身份位置交换时，绑定从相反 chunk 的对应列读取组件。相同组件类型且语义声明 `IsSymmetric=true` 时，可以沿自然行顺序匹配，再按身份顺序输出。缺少参与组件的语义结果为 false，`Except` 保留这个结果。

仅提供身份的来源以及三至八元绑定使用 `World.OpenRead<T>()` 的类型化 `ReadLookup<T>`，先判断实体具有该组件，再通过 `ReadKnown` 借用其只读值。这些 lookup 与 chunk 列都处于实际查询读取范围内。普通、键、bitmap 筛选共用成员、访问与输出链。

## 集合操作与身份位置

| 操作 | 输出 | 位置与自身配对 | 重复规则 |
| --- | --- | --- | --- |
| `world.Product(A,B)` | `(Entity,Entity)` | 保留输入位置，包含自身组合 | 每个位置组合一次 |
| `world.Within(A)` | `(Entity,Entity)` | 按完整实体身份排序，使用不同实体 | 每个无序对一次 |
| `world.Between(A,B)` | `(Entity,Entity)` | 按完整实体身份排序，使用不同实体 | 输入交叠时每个无序对一次 |

[EntityOrder](../../../src/SomeEngine.ECS.Relationships/Pairing/EntityOrder.cs) 先比较 `Id`，再比较 `Generation`。Within 遍历 chunk 对的上三角部分，同一块从 `first+1` 开始；Between 结合反向输入的 `QueryScope.Contains(entity)` 判断重复方向。输出身份顺序同时确定后续语义的位置。

```csharp
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

using var world = new World();
world.Create(new Score { Value = 3 });
world.Create(new Score { Value = 8 });
world.Create(new Score { Value = 12 });
Query scores = world.Query(new QueryDescription().With<Score>());
var selected = world.Product(scores)
    .Where(new MinimumScore(5)).Except(new MinimumScore(10));
int count = selected.Count(); // 1
bool any = selected.Any();  // true

public struct Score { public int Value; }
public readonly struct MinimumScore(int minimum) : IRelation<Score>
{
    public bool Matches(in Score score) => score.Value >= minimum;
}
```

键实现与已有 `IIndexedComponent<TKey>` 键桶共同工作，见 [键关系查询](Key-Relation-Queries.md)。有限类别的两个标量与许可算法见 [Bitmap 关系](Bitmap-Relation-Queries.md)。

## 显式边与端点投影

[RelationEdges](../../../src/SomeEngine.ECS.Relationships/Pairing/RelationEdges.cs) 从 Relationships 原生存储读取 `RelationEdge<T>`。每条平行边各自具有实体身份。

| 入口 | 实际来源 | 结果顺序 |
| --- | --- | --- |
| `world.Edge(edge)` | 指定边的当前身份 | 单条边 |
| `world.OutgoingEdges<T>(source)` | `Relations<T>.Outgoing` | 出边局部顺序 |
| `world.IncomingEdges<T>(target)` | `Relations<T>.Incoming` | 入边局部顺序 |
| `world.IncidentEdges<T>(endpoint)` | `Relations<T>.Incident` | 原生关联顺序 |
| `world.EdgesBetween<T>(first,second)` | 原生邻接与另一端身份 | 来源邻接局部顺序 |

边来源的 `QueryCount` 为零，枚举持有原生 `RelationRead` 或 `IncidentRelationsRead`。端点语义从同一边的 `RelationEndpoints<T>` 与 payload `T` 读取，`DistinctEndpoints()` 将重复边投影为唯一端点对。立即、Deferred、Maintain 的主值与 Applied 邻接顺序见 [Hierarchy](Hierarchy.md) 及原生 [Relations](../../../src/SomeEngine.ECS.Relationships/Relations/Relations.cs)。

## 生成入口、依赖与扩展

[RelationFamilyGenerator](../../../src/SomeEngine.ECS.SourceGen/RelationFamilyGenerator.cs) 在 Relationships 构建时生成八组固定族：`IRelation`、`ComponentRelation`、`ComponentWhere`、`ComponentExcept`、`EntityProduct1` 至 `EntityProduct8`，以及一个 `RelationQueryViews`。这些类型按元数和行为参数化，共 41 个固定类型。`SomeEngineGenerateRelationFamilies` 由 Relationships 项目启用。

业务语义的 `RelationGenerator`、边的 `RelationEdgeGenerator`、键的 `RelationImplementationGenerator`、`BitmapRelationGenerator` 与 `SpatialRelationGenerator` 生成薄扩展入口，返回库内共享泛型类型。扩展点继续使用具体 struct 与类型化列绑定。

独立使用方引用 Core、Relationships 与 SourceGen analyzer：

```xml
<ProjectReference Include="../../src/SomeEngine.ECS/SomeEngine.ECS.csproj" />
<ProjectReference Include="../../src/SomeEngine.ECS.Relationships/SomeEngine.ECS.Relationships.csproj" />
<ProjectReference Include="../../src/SomeEngine.ECS.SourceGen/SomeEngine.ECS.SourceGen.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

公开行为位于 [Relations.Tests](../../../tests/SomeEngine.ECS.Relations.Tests/)，包含一至八元、重复组件位置、托管值读取、组合筛选、身份顺序和提前结束。通用空间候选同在 Relationships，命名空间为 `SomeEngine.ECS.Spatial`；节点摘要见 [空间筛选](Spatial-Filtering.md)，维护见 [空间维护](Spatial-Maintenance.md)，ExecuteRanges 分区见 [空间并行查询](Spatial-Parallel-Queries.md)，独立包使用方见 [包与 NativeAOT](Relation-Packages-and-AOT.md)。
