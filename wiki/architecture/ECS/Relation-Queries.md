# Relation Queries

## 语义关系查询

`SomeEngine.ECS.Relations` 通过公开 ECS 查询 API 扩展关系枚举。当前一元关系使用 `IRelation<T>.Matches(in T)` 定义语义，`world.Product(query)` 枚举原有 `QueryHandle` 的实体成员；一元身份行采用 `Entity`。

关系描述是普通 struct，保存 World、原查询身份、具体枚举类型和语义参数。`Where`、`Except` 逐层收集组件读取需求，终端获取一次派生查询并在结束时释放这次获取。原查询由调用方按既有生命周期释放。派生定义通过 `WithOptionalRead<T>` 合并读取权限，保持 All、Any、None、Optional、启用、共享和变更条件原有的成员含义。

执行沿用 `World.ExecuteQuery<TExecution>` 的资源访问、版本与查询进入/结束流程。回调内的 `RelationQueryViews` 提供已有查询视图，`ComponentRelation<TRelation,TComponent>` 通过 `TryGetRow`、`Has`、`ReadRef` 取得只读组件引用。组件缺失时关系判断为 false，`Where` 保留判断成立的行，`Except` 保留其余行。携带托管引用的 struct 组件沿用相同读取方式。

`RelationQuery<TEnumerable,TRow>` 实现 `IRelationEnumerable<TRow>`。`Count()`、`Any()` 使用具体值类型输出；`Enumerate(ref output)` 接受自定义 `IRelationOutput<TRow>`。输出 `Add` 返回 true 表示继续，false 表示提前结束；枚举返回 true 表示自然完成，false 表示提前结束。泛型输出转发在每层结束时回写输出状态，支持带 `Span` 的 ref struct 输出。组件绑定、查询视图和筛选输出均在实际回调期间持有借用。

`Count(lastSystemVersion)`、`Any(lastSystemVersion)` 和 `Enumerate(lastSystemVersion, ref output)` 接受既有系统版本基线，用于 Added/Changed 等成员筛选。只读关系执行保持 World 时钟及组件变更版本。

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

`IQueryRelationEnumerable<TRow>` 保存参与查询的位置接口：`GetQuery(position)` 返回既有集合身份，`AddReads(definition, position)` 累积该位置的语义读取需求，`Enumerate(RelationQueryViews, ref output)` 在已进入的查询视图中执行具体枚举。后续多元位置积通过这一接口扩展参与位置与视图；空间来源可以提供自己的具体枚举，并复用同一泛型输出。多参与查询的外层访问范围收集全部参与查询及语义读取，随后进入各自保留成员条件的查询作用域；资源范围与版本继续由现有 ECS/WorldJobs 执行。

## 已存储关系边

Direct relation query 以 edge entity 为 row identity，一条 edge 一行。Endpoint filters 是 semi-join：一个 endpoint entity 至多产生一行，不因 parallel edges 自动展开。

需要 endpoint-edge expansion 时必须显式选择，并在可写场景证明不会从两个 endpoint 路径重复写同一 edge。
