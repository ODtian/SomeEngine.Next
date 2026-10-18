using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using System.Runtime.CompilerServices;

namespace SomeEngine.Tools.RelationsSmoke;

// Business example: eight positions read the same closed generic component type.
public struct Number<TUnit> : IComponent where TUnit : unmanaged
{
    public int Value;
}

public struct Position1 : ITag;
public struct Position2 : ITag;
public struct Position3 : ITag;
public struct Position4 : ITag;
public struct Position5 : ITag;
public struct Position6 : ITag;
public struct Position7 : ITag;
public struct Position8 : ITag;

public readonly struct Total1(int minimum) : IRelation<Number<int>>
{
    public bool Matches(in Number<int> first) => first.Value >= minimum;
}

public readonly struct Total2(int minimum) : IRelation<Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second) => first.Value + second.Value >= minimum;
}

public readonly struct Total3(int minimum) : IRelation<Number<int>, Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third) =>
        first.Value + second.Value + third.Value >= minimum;
}

public readonly struct Total4(int minimum) : IRelation<Number<int>, Number<int>, Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third, in Number<int> fourth) =>
        first.Value + second.Value + third.Value + fourth.Value >= minimum;
}

public readonly struct Total5(int minimum) : IRelation<Number<int>, Number<int>, Number<int>, Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third, in Number<int> fourth,
        in Number<int> fifth) => first.Value + second.Value + third.Value + fourth.Value + fifth.Value >= minimum;
}

public readonly struct Total6(int minimum) : IRelation<Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third, in Number<int> fourth,
        in Number<int> fifth, in Number<int> sixth) =>
        first.Value + second.Value + third.Value + fourth.Value + fifth.Value + sixth.Value >= minimum;
}

public readonly struct Total7(int minimum) : IRelation<Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>>
{
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third, in Number<int> fourth,
        in Number<int> fifth, in Number<int> sixth, in Number<int> seventh) =>
        first.Value + second.Value + third.Value + fourth.Value + fifth.Value + sixth.Value + seventh.Value >= minimum;
}

public readonly struct WeightedTotal8(int minimum) : IRelation<Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>, Number<int>>
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Matches(in Number<int> first, in Number<int> second, in Number<int> third, in Number<int> fourth,
        in Number<int> fifth, in Number<int> sixth, in Number<int> seventh, in Number<int> eighth) =>
        first.Value + 2 * second.Value + 3 * third.Value + 4 * fourth.Value + 5 * fifth.Value +
        6 * sixth.Value + 7 * seventh.Value + 8 * eighth.Value >= minimum;
}

public struct IncreaseNumbers : IJobEntity
{
    public void Execute(ref Number<int> number) => number.Value++;
}

[RelationSchema(RelationDirection.Directed)]
public struct Wire<T> : IComponent where T : unmanaged;

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair, AllowSelfEdge = false)]
public struct Joint<T> : IComponent where T : unmanaged;

public struct NativeAddress<T> : IComponent where T : unmanaged
{
    public nint Address { get; set; }
}

internal ref struct EighthEntity : IRelationOutput<(Entity, Entity, Entity, Entity, Entity, Entity, Entity, Entity)>
{
    private readonly Span<Entity> _result;

    internal EighthEntity(Span<Entity> result)
    {
        _result = result;
        Count = 0;
    }

    internal int Count;

    public bool Add(in (Entity, Entity, Entity, Entity, Entity, Entity, Entity, Entity) row)
    {
        _result[Count++] = row.Item8;
        return false;
    }
}
