# Relation Queries

## 语义关系查询

`SomeEngine.ECS.Relations` 通过公开 ECS 查询 API 扩展关系枚举。一至八元关系使用 `IRelation<T1,…,Tn>.Matches(in …)` 定义语义。参与集合使用原有 `QueryHandle`；一元身份行采用 `Entity`，二至八元身份行采用相应的标准实体 `ValueTuple`。八元行可直接使用 `row.Item8` 读取第八个实体。

关系描述是普通 struct，保存 World、原查询身份、具体枚举类型和语义参数。终端获取一个外层访问查询及各参与查询，在结束时逐个释放本次获取。原查询由调用方按既有生命周期释放。参与查询使用原定义，保留 All、Any、None、Optional、启用、共享和变更条件原有的成员含义。

`QueryDefinition.WithAccessFrom` 将两份定义的存储访问归一化为可选访问并集，其中包含变更、启用等过滤条件所需的读取。`Where`、`Except` 通过 `WithOptionalRead<T>` 将签名读取补入这一并集。外层查询持有完整访问范围，内层嵌套各参与集合的成员查询，执行沿用 `World.ExecuteQuery<TExecution>` 的资源访问、版本与查询进入/结束流程。

回调内的 `RelationQueryViews.Access` 提供完整读取视图，`First` 至 `Eighth` 保留输入集合各自的成员视图。共享 `ComponentRelation<…>` 从 Access 通过 `TryGetRow`、`Has`、`ReadRef` 取得组件只读引用，再分别传入具体关系语义。组件缺失时关系判断为 false，`Where` 保留判断成立的行，`Except` 保留其余行。携带托管引用的 struct 组件沿用相同读取方式。`Between` 在排序后的实体位置上读取组件，因此两个输入集合原来的角色可以交换。

`RelationQuery<TEnumerable,TRow>` 实现 `IRelationEnumerable<TRow>`。`Count()`、`Any()` 使用具体值类型输出；`Enumerate(ref output)` 接受自定义 `IRelationOutput<TRow>`。输出 `Add` 返回 true 表示继续，false 表示提前结束；枚举返回 true 表示自然完成，false 表示提前结束。泛型输出转发在每层结束时回写输出状态，支持带 `Span` 的 ref struct 输出。组件绑定、查询视图和筛选输出均在实际回调期间持有借用。

`Count(lastSystemVersion)`、`Any(lastSystemVersion)` 和 `Enumerate(lastSystemVersion, ref output)` 接受既有系统版本基线，用于 Added/Changed 等成员筛选。只读关系执行保持 World 时钟及组件变更版本。

### 二元集合操作

| 操作 | 输出行 | 位置顺序 | 自身配对 | 重复规则 |
| --- | --- | --- | --- | --- |
| `world.Product(A,B)` | `(Entity, Entity)` | 保留输入集合位置 | 保留 | 每个位置组合一次 |
| `world.Within(A)` | `(Entity, Entity)` | 完整实体身份升序 | 排除 | 每个无序实体对一次 |
| `world.Between(A,B)` | `(Entity, Entity)` | 完整实体身份升序 | 排除 | 集合交叠时每个无序实体对一次 |

`EntityOrder.Compare` 先比较 Index，再比较 Generation。`Within` 从当前行枚举器复制剩余范围，一次访问每个候选对，再排列输出身份。`Between` 通过相反输入的成员视图识别已有反向组合，保留只有一个输入方向的候选。排序后的身份行决定随后关系语义的输入位置。

完整 Body/Hair 业务代码、13 个实体对及对应生成入口见 [二元关系业务例子](Relation-Query-Example.md)。

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

`IQueryRelationEnumerable<TRow>` 保存参与查询的位置接口：`QueryCount` 表示输入查询数量，`GetQuery(position)` 返回既有集合身份，`AddReads(definition, position)` 累积该位置的语义读取需求，`Enumerate(RelationQueryViews, ref output)` 在已进入的查询视图中执行具体枚举。`Within` 的 QueryCount 为 1，其行元数为 2；`Product(A,B)` 与 `Between(A,B)` 的 QueryCount 为 2。

共享执行体以输入位置递归进入既有 World 查询回调，值类型句柄缓冲保存八个参与查询及其外层访问查询。每层在结束时回写泛型输出。固定泛型族和查询视图由同一库构建生成器覆盖一至八元；使用方生成器按接口元数组装标准实体元组与共享泛型类型。空间来源可以提供自己的具体枚举，并复用 QueryCount、Access 与同一泛型输出；资源范围与版本继续由现有 ECS/WorldJobs 执行。

## 已存储关系边

Direct relation query 以 edge entity 为 row identity，一条 edge 一行。Endpoint filters 是 semi-join：一个 endpoint entity 至多产生一行，不因 parallel edges 自动展开。

需要 endpoint-edge expansion 时必须显式选择，并在可写场景证明不会从两个 endpoint 路径重复写同一 edge。
