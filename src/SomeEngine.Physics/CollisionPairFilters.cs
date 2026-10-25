using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public interface ICollisionPairFilter
{
    QueryDefinition AddReads(QueryDefinition definition);
    bool Matches(scoped QueryRow first, scoped QueryRow second);
}

public readonly struct AllCollisionPairs : ICollisionPairFilter
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition;
    public bool Matches(scoped QueryRow first, scoped QueryRow second) => true;
}

public readonly struct CollisionComponentFilter<TRelation, TFirst, TSecond>(TRelation relation)
    : ICollisionPairFilter
    where TRelation : struct, IRelation<TFirst, TSecond>
    where TFirst : struct
    where TSecond : struct
{
    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<TFirst>().WithOptionalRead<TSecond>();
    public bool Matches(scoped QueryRow first, scoped QueryRow second)
    {
        var bound = new ComponentRelation<TRelation, TFirst, TSecond>(default, relation);
        return bound.Matches(first, second);
    }
}

public readonly struct CollisionKeyFilter<TRelation, TImplementation, TComponent, TKey>(
    TRelation relation, TImplementation implementation) : ICollisionPairFilter
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<TComponent>();
    public bool Matches(scoped QueryRow first, scoped QueryRow second)
    {
        var bound = new KeyRelation<TRelation, TImplementation, TComponent, TKey>(default, relation, implementation);
        return bound.Matches(default, first, second);
    }
}

public readonly struct CollisionBitmapFilter<TRelation, TImplementation, TComponent, TPermission>(
    TRelation relation, TImplementation implementation) : ICollisionPairFilter
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct
    where TPermission : struct, ICategoryPermission
{
    public QueryDefinition AddReads(QueryDefinition definition) => definition.WithOptionalRead<TComponent>();
    public bool Matches(scoped QueryRow first, scoped QueryRow second)
    {
        var bound = new BitmapRelation<TRelation, TImplementation, TComponent, TPermission>(default, relation, implementation);
        return bound.Matches(default, first, second);
    }
}

public readonly struct CollisionPairAnd<TFirst, TSecond>(TFirst first, TSecond second) : ICollisionPairFilter
    where TFirst : struct, ICollisionPairFilter
    where TSecond : struct, ICollisionPairFilter
{
    public QueryDefinition AddReads(QueryDefinition definition) => second.AddReads(first.AddReads(definition));
    public bool Matches(scoped QueryRow firstRow, scoped QueryRow secondRow) =>
        first.Matches(firstRow, secondRow) && second.Matches(firstRow, secondRow);
}

public readonly struct CollisionPairExcept<TFilter>(TFilter filter) : ICollisionPairFilter
    where TFilter : struct, ICollisionPairFilter
{
    public QueryDefinition AddReads(QueryDefinition definition) => filter.AddReads(definition);
    public bool Matches(scoped QueryRow first, scoped QueryRow second) => !filter.Matches(first, second);
}
