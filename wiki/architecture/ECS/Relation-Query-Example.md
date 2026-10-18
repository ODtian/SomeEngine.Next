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

这两个业务关系共生成四个薄入口：每个关系分别具有 `Where` 与 `Except`。它们合入同一个 `RelationQueryExtensions` 类；业务组件专属生成类型数量为 0。上述调用使用的两个入口形状如下，其组件类型参数来自关系签名。

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
