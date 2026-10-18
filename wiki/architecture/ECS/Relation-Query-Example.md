# 二元关系业务例子

此页的 `MotionKind`、`Body`、`Hair`、`DefaultContacts`、`SameCharacterHair` 是演示通用关系 API 的业务例子。集合、作用域、组件绑定与输出来自 [关系查询](Relation-Queries.md) 的库 API；业务方法直接读取组件字段。

## 完整使用方代码

以下代码建立 A1、A2、B、S、K、D 六个物体。A1、A2 属于同一角色，B 属于另一角色，三个头发物体均为 Dynamic。S、K、D 分别为普通 Static、Kinematic、Dynamic。

```csharp
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

using var world = new World();
var characterA = world.CreateEntity();
var characterB = world.CreateEntity();
var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
var s = world.CreateEntity(new Body { Kind = MotionKind.Static });
var k = world.CreateEntity(new Body { Kind = MotionKind.Kinematic });
var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
world.Add(a1, new Hair { Character = characterA });
world.Add(a2, new Hair { Character = characterA });
world.Add(b, new Hair { Character = characterB });

var bodies = world.Query(world.QueryDefinition().All<Body>());
int count = world.Within(bodies)
    .Where(default(DefaultContacts))
    .Except(default(SameCharacterHair))
    .Count();
world.ReleaseQuery(bodies);
Console.WriteLine(count); // 13

public enum MotionKind { Dynamic, Static, Kinematic }

public struct Body : IComponent
{
    public MotionKind Kind;
}

public struct Hair : IComponent
{
    public Entity Character;
}

public readonly struct DefaultContacts : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second) =>
        first.Kind == MotionKind.Dynamic || second.Kind == MotionKind.Dynamic;
}

public readonly struct SameCharacterHair : IRelation<Hair, Hair>
{
    public bool Matches(in Hair first, in Hair second) => first.Character == second.Character;
}
```

完整结果为 A1–B、A1–S、A1–K、A1–D、A2–B、A2–S、A2–K、A2–D、B–S、B–K、B–D、S–D、K–D。两个不同角色的头发接受默认规则；缺少 Hair 的普通物体继续参与查询。可执行使用方及独立期望行位于 `tests/SomeEngine.ECS.Relations.Tests/CollisionRelationExampleTests.cs`。

## 生成入口

这两个二元业务关系共生成八个薄入口：每个关系的实体对形式与显式边形式分别具有 `Where` 与 `Except`。它们合入同一个 `RelationQueryExtensions` 类；业务组件专属生成类型数量为 0。上述调用使用实体对形式，所用的两个入口形状如下，其组件类型参数来自关系签名。显式边形式见 [已存储关系边](Relation-Queries.md#已存储关系边)。

```csharp
public static partial class RelationQueryExtensions
{
    public static RelationQuery<ComponentWhere<TEnumerable, DefaultContacts, Body, Body>, (Entity, Entity)>
        Where<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> source, DefaultContacts relation)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        => new(new ComponentWhere<TEnumerable, DefaultContacts, Body, Body>(source.Enumerable, relation));

    public static RelationQuery<ComponentExcept<TEnumerable, SameCharacterHair, Hair, Hair>, (Entity, Entity)>
        Except<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> source, SameCharacterHair relation)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        => new(new ComponentExcept<TEnumerable, SameCharacterHair, Hair, Hair>(source.Enumerable, relation));
}
```

共享组件绑定在执行回调中分别读取两个实体的 `ref readonly` 组件，并调用具体 `Matches(in first, in second)`。计数、存在性与自定义输出均保留源枚举、关系和输出的具体泛型类型。

## 可选角色键实现

以下类型仍属于业务例子。优化版本将 `Hair` 的声明替换为索引组件，并补充 `HairCharacterIndex`；关系库的通用设计通过 `IKeyRelation` 表达语义与键实现的类型关联。

```csharp
using SomeEngine.ECS.Components;

public struct Hair : IComponent, IIndexedComponent<Entity>
{
    public Entity Character;
    public readonly Entity GetKey() => Character;
}

public readonly struct HairCharacterIndex : IKeyRelation<SameCharacterHair, Hair, Entity>
{
    public bool Matches(in SameCharacterHair relation, in Entity first, in Entity second) => first == second;
}
```

使用相同的 World、六个物体和 `bodies` 查询，可以显式选择角色键实现。直接从 `Within` 附加键规则时，具体枚举按已有桶形成键组；已有普通组合链也接受相同的语义与实现参数。

```csharp
int directCount = world.Within(bodies)
    .Where(default(DefaultContacts))
    .Except(default(SameCharacterHair))
    .Count();

int indexedCount = world.Within(bodies)
    .Except(default(SameCharacterHair), default(HairCharacterIndex))
    .Where(default(DefaultContacts))
    .Count();

int composedCount = world.Within(bodies)
    .Where(default(DefaultContacts))
    .Except(default(SameCharacterHair), default(HairCharacterIndex))
    .Count();
// 三者均为 13，输出对应相同的十三个实体对。
```

此键实现产生八个额外入口，业务组件专属生成类型数量为零。直接 `Within` 与普通组合链的两个实际入口如下；其余入口按 `Where`、`Between`、二元 `Product` 的对应具体来源生成。

```csharp
public static partial class RelationQueryExtensions
{
    public static RelationQuery<KeyPairs<KeyPairsWithin, SameCharacterHair, HairCharacterIndex, Hair, Entity>, (Entity, Entity)>
        Except(this RelationQuery<EntityPairsWithin, (Entity, Entity)> source,
            SameCharacterHair relation, HairCharacterIndex implementation)
        => new(new(source.Enumerable.World, source.Enumerable.GetQuery(0), source.Enumerable.GetQuery(0),
            relation, implementation, true));

    public static RelationQuery<KeyFilter<TEnumerable, SameCharacterHair, HairCharacterIndex, Hair, Entity>, (Entity, Entity)>
        Except<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> source,
            SameCharacterHair relation, HairCharacterIndex implementation)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        => new(new(source.Enumerable, relation, implementation, true));
}
```

键数据维护、生成规则和实际手写桶循环见[键关系查询](Key-Relation-Queries.md)。可执行的业务类型位于 `CollisionRelationExample.cs`，键实现与手写对照的行为检查位于 `KeyRelationTests.cs`。
