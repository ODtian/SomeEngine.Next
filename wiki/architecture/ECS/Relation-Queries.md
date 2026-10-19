# Relation Queries

## 语义关系查询

`SomeEngine.ECS.Relations` 通过公开 ECS 查询 API 扩展关系枚举。一至八元关系使用 `IRelation<T1,…,Tn>.Matches(in …)` 定义语义。参与集合使用原有 `QueryHandle`；一元身份行采用 `Entity`，二至八元身份行采用相应的标准实体 `ValueTuple`。八元行可直接使用 `row.Item8` 读取第八个实体。

关系描述是普通 struct，保存 World、原查询身份、具体枚举类型和语义参数。终端按具体来源取得成员与组件读取查询，在结束时释放本次获取。原查询由调用方按既有生命周期释放。参与查询使用原定义，保留 All、Any、None、Optional、启用、共享和变更条件原有的成员含义。

`QueryDefinition.WithAccessFrom` 将两份定义的存储访问归一化为可选访问并集，其中包含变更、启用等过滤条件所需的读取。`Where`、`Except` 通过 `WithOptionalRead<T>` 将签名读取补入这一并集。外层查询持有完整访问范围，内层嵌套各参与集合的成员查询，执行沿用 `World.ExecuteQuery<TExecution>` 的资源访问、版本与查询进入/结束流程。

静态 `ReadsFromSingleQuery` 为 true 的来源声明所有输出参与实体都属于唯一输入集合。该集合只有只读访问时，终端在原成员定义上累积完整语义读取，用一次 `ExecuteQuery` 同时提供成员与组件视图。Within、键桶 Within 和空间单集合来源采用此路径。多集合、写入集合、普通实体持有的多元事实及原生边来源按各自读取范围执行。`WithOptionalRead` 在现有访问已经包含读取时复用当前定义。

单成员只读选择入口只形成该分支的定义与执行状态；多集合和可写路径在其独立方法中形成查询身份数组、嵌套视图与输出状态。两条路径分别沿原版本参数执行，完成后回写输出，并按取得顺序的逆序释放查询。数据形成范围与实际进入的读取范围一致。

回调内的 `RelationQueryViews.Access` 提供完整读取视图，`First` 至 `Eighth` 保留输入集合各自的成员视图。共享 `ComponentRelation<…>` 从 Access 通过 `TryGetRow` 取得实体行，或接收来源已经取得的行，再通过 `TryReadRef` 取得组件只读引用，分别传入具体关系语义。组件缺失时关系判断为 false，`Where` 保留判断成立的行，`Except` 保留其余行。携带托管引用的 struct 组件沿用相同读取方式。`Between` 在排序后的实体位置上读取组件，因此两个输入集合原来的角色可以交换。

`QueryRow.TryReadRef<T>(out ComponentReference<T> component)` 在已编译的读取访问表中同时查找组件存在性和列位置。存在的组件沿 `QueryAccessGuards` 检查声明权限，缺失的组件返回 false。`ComponentReference<T>` 是仅持一个 `ref readonly T` 的回调借用，`Value` 继续返回同一只读引用。原组件、查询定义和访问表分别由 World 与既有查询状态持有。

`RelationQuery<TEnumerable,TRow>` 实现 `IRelationEnumerable<TRow>`。`Count()`、`Any()` 使用具体值类型输出；`Enumerate(ref output)` 接受自定义 `IRelationOutput<TRow>`。输出 `Add` 返回 true 表示继续，false 表示提前结束；枚举返回 true 表示自然完成，false 表示提前结束。泛型输出转发在每层结束时回写输出状态，支持带 `Span` 的 ref struct 输出。组件绑定、查询视图和筛选输出均在实际回调期间持有借用。

查询内部的 `IQueryRelationOutput<TRow>` 与 `IQueryRelation<TRow>` 为二元行增加显式 `scoped in QueryRow` 回调。来源把已经取得的两行按最终身份位置传给组件、键、位图绑定，后续筛选继续传递同一组行。来源只有身份行时沿原组件绑定读取。公开终端通过一次 `QueryIdentityOutput` 将结果交给原 `IRelationOutput.Add(in identity)`；用户的 Span 输出继续实现这一原有成员。事实实体和显式边的身份位置按各自语义绑定参与实体。

`Count(lastSystemVersion)`、`Any(lastSystemVersion)` 和 `Enumerate(lastSystemVersion, ref output)` 接受既有系统版本基线，用于 Added/Changed 等成员筛选。只读关系执行保持 World 时钟及组件变更版本。

### 二元集合操作

| 操作 | 输出行 | 位置顺序 | 自身配对 | 重复规则 |
| --- | --- | --- | --- | --- |
| `world.Product(A,B)` | `(Entity, Entity)` | 保留输入集合位置 | 保留 | 每个位置组合一次 |
| `world.Within(A)` | `(Entity, Entity)` | 完整实体身份升序 | 排除 | 每个无序实体对一次 |
| `world.Between(A,B)` | `(Entity, Entity)` | 完整实体身份升序 | 排除 | 集合交叠时每个无序实体对一次 |

`EntityOrder.Compare` 先比较 Index，再比较 Generation，提供三向排序结果；`EntityOrder.Precedes` 按同一完整身份顺序直接判断先后。`Within` 从当前行枚举器复制剩余范围，一次访问每个候选对，再排列输出身份。`Between` 通过相反输入的成员视图识别已有反向组合，保留只有一个输入方向的候选。排序后的身份行决定随后关系语义的输入位置。

二元普通语义另有接收 `EntityPairsWithin` 的生成薄入口，返回共享 `ComponentPairsWithin<TRelation,TFirst,TSecond>`。该枚举缓存外层行的组件引用，内层直接读取已经取得的行；身份换位时组件位置一同换位。Where、Except、计数、存在性与提前结束共用这条具体链。

二元 `IRelation<TFirst,TSecond>.IsSymmetric` 是关系作者提供的静态语义事实，默认值为 false。当两个组件类型相同且作者声明 true，交换两个输入保持 Matches 结果，Within 因此按自然行顺序匹配，再将接受的身份对排列成完整 Entity 升序。默认运动规则的业务例子声明这一事实。其余关系按排序后的身份确定组件位置再调用 Matches。该能力由固定二元接口持有，生成入口继续薄转发。

完整 Body/Hair 业务代码、13 个实体对及对应生成入口见 [二元关系业务例子](Relation-Query-Example.md)。

通过 `IIndexedComponent<TKey>` 与既有键桶提供的可选实现见[键关系查询](Key-Relation-Queries.md)。

通过类别位数据与具体单向、双方许可操作提供的可选实现见[有限类别关系](Bitmap-Relation-Queries.md)。

### 业务示例

以下分数规则是演示一元 API 的业务例子。`Score` 和 `MinimumScore` 属于业务代码；`Product`、`Count`、`Any` 与共享组件绑定属于关系库。

```csharp
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;

using var world = new World();
world.CreateEntity(new Score { Value = 3 });
world.CreateEntity(new Score { Value = 8 });
world.CreateEntity(new Score { Value = 12 });
var scores = world.Query(world.QueryDefinition().All<Score>());

var selected = world.Product(scores)
    .Where(new MinimumScore(5))
    .Except(new MinimumScore(10));
int count = selected.Count(); // 1
bool any = selected.Any();  // true
world.ReleaseQuery(scores);

public struct Score : IComponent
{
    public int Value;
}

public readonly struct MinimumScore(int minimum) : IRelation<Score>
{
    public bool Matches(in Score score) => score.Value >= minimum;
}
```

独立使用方引用 `SomeEngine.ECS`、`SomeEngine.ECS.Relations`，并显式将 `SomeEngine.ECS.SourceGen` 作为 Analyzer 引入。仓库内对应公开使用方为 `tests/SomeEngine.ECS.Relations.Tests`。

```xml
<ProjectReference Include="../../src/SomeEngine.ECS/SomeEngine.ECS.csproj" />
<ProjectReference Include="../../src/SomeEngine.ECS.Relations/SomeEngine.ECS.Relations.csproj" />
<ProjectReference Include="../../src/SomeEngine.ECS.SourceGen/SomeEngine.ECS.SourceGen.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

### 生成入口与扩展位置

`RelationGenerator` 从 Roslyn 关系接口与方法签名取得组件类型，正常 C# 编译负责语义方法体。每个业务关系产生薄的 `Where`、`Except` 方法，方法返回共享泛型绑定；同一命名空间中的方法合入一个 `RelationQueryExtensions` 类。上述单关系示例产生两个入口方法，业务组件专属生成类型数量为零。库中的组件绑定和输出转发按泛型类型共享。

二元语义再产生两个 Within 具体来源重载，仍返回同一共享泛型族；其它来源继续使用通用重载。

`SomeEngine.ECS.Relations.csproj` 通过 `SomeEngineGenerateRelationFamilies` 构建属性启用 `RelationFamilyGenerator`。库构建统一生成一至八元固定族，使用方从实际关系程序集引用这些类型。每个元数的位置积采用展开的嵌套循环，绑定对每个语义输入分别传入 `ref readonly` 组件。

固定生成清单为：

- `IRelation<…>`：8 个泛型接口。
- `ComponentRelation<TRelation,…>`：8 个泛型组件绑定。
- `ComponentWhere<TEnumerable,TRelation,…>`：8 个泛型筛选描述。
- `ComponentExcept<TEnumerable,TRelation,…>`：8 个泛型排除描述。
- `EntityProduct` 与 `EntityProduct2` 至 `EntityProduct8`：8 个位置积描述，对应 `RelationQueries` 的 8 个 `Product` 重载。
- `RelationQueryViews`：1 个含 Access 与八个成员位置的借用视图。

上述四组泛型族合计 32 个泛型类型，连同位置积和查询视图为 41 个固定类型；`Product` 方法合入既有操作类。每份库构建产生八份元数源码和一份查询视图源码。使用方的业务组件专属生成类型数量为 0。

`tests/SomeEngine.ECS.Relations.Tests/MultiRelationTests.cs` 是独立使用方的多元行为检查，覆盖三至八元、不同参与集合、重复组件类型、托管组件、筛选、排除、计数、存在性及八元输出提前结束。这组业务例子定义 12 个语义类型，对应 24 个薄入口，合入同一扩展类；一元与二元行为沿用同项目的公开使用方检查。

`IQueryRelationEnumerable<TRow>` 保存参与查询的位置接口：`QueryCount` 表示输入查询数量，`GetQuery(position)` 返回既有集合身份，`AddReads(definition, position)` 累积该位置的语义读取需求，`Enumerate(RelationQueryViews, ref output)` 在已进入的查询视图中执行具体枚举。`Within` 的 QueryCount 为 1，其行元数为 2；`Product(A,B)` 与 `Between(A,B)` 的 QueryCount 为 2。原生邻接来源的 QueryCount 为 0；执行体调用一次 `AddReads(definition, 0)`，并在外层 Access 查询中枚举。零个或一个输入查询的绑定将全部语义位置的组件读取加入 Access。

共享执行体以输入位置递归进入既有 World 查询回调，值类型句柄缓冲保存八个参与查询及其外层访问查询。每层在结束时回写泛型输出。固定泛型族和查询视图由同一库构建生成器覆盖一至八元；使用方生成器按接口元数组装标准实体元组与共享泛型类型。空间来源可以提供自己的具体枚举，并复用 QueryCount、Access 与同一泛型输出；资源范围与版本继续由现有 ECS/WorldJobs 执行。

`SomeEngine.Physics` 的真实二叉 AABB 空间来源、复用树的调用与手写宽相对照见 [空间候选](Spatial-Candidates.md)。

类别和角色键的节点摘要、逐条件证明及空间薄入口见 [空间筛选下推](Spatial-Filtering.md)；排序、范围、多路连接及其它空间实现的接入数据见 [关系实现扩展](Relation-Query-Extensions.md)。

### 空间执行 API 扩展去向

空间描述的 `Prepare` 和节点对枚举进入 `ICollisionPartitionEnumerable`；`FillPairs` 与 `FillPairsParallel` 通过原有 RelationQuery 读取范围接入单线程和并行计数／前缀／填充。两种终端都提供显式 lastSystemVersion 重载。`CollisionPairBuffer.Count/Pairs` 交付真实输出范围，FillAccess 和 ReadAccess 交给对应 Scheduler 工作。Systems 的 `ReadOnlyQueryPairJobs` 提供回调期间的并行借用与同步 join。生命周期、实际输出区间和成对业务调用见 [空间关系分区执行](Spatial-Parallel-Queries.md)。实际包依赖、构建时元数据及原生使用方见 [关系包与 NativeAOT](Relation-Packages-and-AOT.md)。

## 查询回调中的组件引用

`QueryCursor.Contains(Entity)` 检查本次查询的成员事实。它和 `TryGetRow` 复用 World 的点匹配流程，应用相同组件集合、共享条件、enabled、chunk 条件、逐行 Changed 与 LastSystemVersion。键桶和 Between 的纯成员确认使用 Contains；需要后续组件引用的链继续取得 QueryRow，并沿借用输出转发。

`QueryRow.TryReadRef<T>(out ComponentReference<T>)` 在一次访问声明查找后借用实际组件引用。`ComponentReference<T>.HasValue` 表达可选组件的存在性，`Value` 返回 `ref readonly T`。`QueryCursor.ReadRows<TFirst,TSecond>()` 沿原有行枚举取得成员，在块边界绑定这两个组件的可选列；`MoveNext(out row,out first,out second)` 同时交付原行及两个引用。读取声明由原查询的 `WithOptionalRead<T>()` 形成，成员、共享条件、enabled 和 Changed 仍使用原查询筛选。引用与枚举器的有效期均为当前 `World.ExecuteQuery` 回调。

`QueryCursor.Requires<T>()` 从本次执行的原 QueryDefinition 读取必有事实：All 声明该组件，或整个 Any 集合只有该组件。二元 Within 在枚举入口读取两位置的事实并闭合 Required/Optional 组件模式，逐对匹配使用这一已有不变量。可选模式保留缺组件关系为 false 和 Except 保留该行的行为。定义继续由原 QueryState 持有。

`Within(...).Where/Except(...)` 的二元组件入口直接使用这些已有行和引用。后续规则通过查询专用的 `IQueryRelationOutput` 和 `IQueryRelation` 转发借用行，公开终端仍通过 `IRelationOutput.Add(in identity)` 接收身份元组。固定候选、键桶和空间来源同样复用已取得的成员行；存储事实及边保持各自参与者来源和访问读取。

## 已存储关系边

显式边来源使用 `RelationEdge<T>` 作为身份行，每条平行边各自输出。来源描述是普通 struct，保存 World 与端点参数；终端执行时读取原有邻接或端点间边视图。

| 入口 | 具体来源 | 成员来源 | 行顺序 |
| --- | --- | --- | --- |
| `world.Edge(edge)` | `SingleRelationEdge<T>` | 指定的存活边实体 | 单条边 |
| `world.OutgoingEdges<T>(source)` | `OutgoingRelationEdges<T>` | source 的原生出边邻接 | 原生出边局部顺序 |
| `world.IncomingEdges<T>(target)` | `IncomingRelationEdges<T>` | target 的原生入边邻接 | 原生入边局部顺序 |
| `world.IncidentEdges<T>(endpoint)` | `IncidentRelationEdges<T>` | endpoint 的原生关联边邻接 | 原生关联边局部顺序 |
| `world.EdgesBetween<T>(first,second)` | `RelationEdgesBetween<T>` | 原生端点间边查询 | 原生来源邻接局部顺序 |

这些来源返回 `RelationQuery<具体来源,RelationEdge<T>>`，共用 `Enumerate`、`Count`、`Any` 与提前结束输出。创建、立即重定向、销毁、基数约束与端点角色继续由原有显式边实现持有；有序邻接的插入及重排直接体现在来源顺序中。

二元语义可直接用于边的 `Where` 与 `Except`。`RelationEdgeGenerator` 为每个二元业务语义另外生成两个薄入口，返回共享的 `EdgeComponentWhere` / `EdgeComponentExcept`。绑定从 Access 读取当前端点组件，构成临时实体位置对，再通过既有 `ComponentRelation<TRelation,T1,T2>` 分别读取语义组件。筛选输出继续传递原始 `RelationEdge<T>`。Directed 使用 Source/Target；Undirected 使用创建或重定向参数的 EndpointA/EndpointB 角色。

邻接成员读取已应用的派生状态，端点语义读取当前端点组件。`RetargetRelationDeferred` 修改后，来源仍使用上次已应用的邻接成员，语义读取新端点；`MaintainRelations<T>` 或 `RelationMaintenanceSystem<T>.Schedule` 完成后，来源切换到更新后的邻接。需要维护结果的 Job 显式依赖维护 handle，端点读取沿用 `RelationJobAccess<T>.EndpointsRead` 与 topology-read；语义组件读取按既有组件资源声明加入。

端点集合通过显式投影得到标准 `(Entity,Entity)` 行：

| 投影 | 位置含义 | 去重键 | 输出顺序 |
| --- | --- | --- | --- |
| `edges.DistinctEndpoints()` | Source/Target 或 EndpointA/EndpointB | 保留位置的完整实体身份对 | 来源首次出现顺序 |
| `edges.DistinctUnorderedEndpoints()` | 完整实体身份升序 | 排序后的完整实体身份对 | 来源首次出现顺序 |

投影使用本次枚举持有的 `HashSet<(Entity,Entity)>` 记录已输出对，结束后随枚举释放。平行边投影到同一端点对时输出一行。保留角色的投影把 `(A,B)` 与 `(B,A)` 分别输出；无序投影把两者输出为同一对。投影结果继续使用普通二元语义、计数、存在性与提前结束输出。

```csharp
var selectedEdges = world.OutgoingEdges<Shipment>(source)
    .Where(new ScoreWeightGap(5))
    .Except(new ScoreWeightGap(9));
int edgeCount = selectedEdges.Count();
int pairCount = selectedEdges.DistinctEndpoints().Count();
```

这里的 `Shipment` 与 `ScoreWeightGap` 是业务例子。完整的公开行为检查位于 `tests/SomeEngine.ECS.Relations.Tests/EdgeRelationTests.cs`，包括平行边、角色局部顺序、端点绑定、输出状态、维护依赖及两种投影。

### 普通实体持有多元事实的业务例子

`StoredRelationFactExample.cs` 演示由一个普通 ECS 实体持有三元 `TransferFact`：Source、Carrier、Destination 三个 Entity 字段保存参与位置。事实实体的创建与销毁沿用 World 普通实体生命周期，事实集合使用 `All<TransferFact>()` 的原查询。业务枚举 `TransferFacts` 在 First 成员视图中遍历事实实体，从 Access 读取组件，再输出 `(Source,Carrier,Destination)`。

```csharp
public struct TransferFact : IComponent
{
    public Entity Source;
    public Entity Carrier;
    public Entity Destination;
}

var facts = world.Query(world.QueryDefinition().All<TransferFact>());
var transfers = world.Transfers(facts).Where(new TransferFits());
int count = transfers.Count();
world.ReleaseQuery(facts);
```

`Transfers`、`TransferFacts` 与 `TransferFits` 都属于该业务例子。它们通过公共 `IQueryRelationEnumerable<(Entity,Entity,Entity)>` 接入共享关系执行体，三元规则读取三个参与位置的组件。完整业务代码和行为检查分别位于 `tests/SomeEngine.ECS.Relations.Tests/StoredRelationFactExample.cs`、`StoredRelationFactTests.cs`。
