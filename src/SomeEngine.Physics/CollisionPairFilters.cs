using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public interface ICollisionPairFilter
{
    QueryDescription AddReads(QueryDescription description);
    bool Matches(World world, Entity first, Entity second);
}
public readonly struct AllCollisionPairs : ICollisionPairFilter
{
    public QueryDescription AddReads(QueryDescription description) => description;
    public bool Matches(World world, Entity first, Entity second) => true;
}
public readonly struct CollisionComponentFilter<TRelation, TFirst, TSecond>(TRelation relation) : ICollisionPairFilter
    where TRelation : struct, IRelation<TFirst, TSecond> where TFirst : struct where TSecond : struct
{
    public QueryDescription AddReads(QueryDescription description) => description.Optional<TFirst>().Optional<TSecond>();
    public bool Matches(World world, Entity first, Entity second) => world.Has<TFirst>(first) && world.Has<TSecond>(second)
        && relation.Matches(in world.Read<TFirst>(first), in world.Read<TSecond>(second));
}
public readonly struct CollisionKeyFilter<TRelation, TImplementation, TComponent, TKey>(TRelation relation, TImplementation implementation)
    : ICollisionPairFilter where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IKeyRelation<TRelation, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey> where TKey : notnull, IEquatable<TKey>
{
    public QueryDescription AddReads(QueryDescription description) => description.Optional<TComponent>();
    public bool Matches(World world, Entity first, Entity second) => world.Has<TComponent>(first) && world.Has<TComponent>(second)
        && relation.Matches(in world.Read<TComponent>(first), in world.Read<TComponent>(second));
}
public readonly struct CollisionBitmapFilter<TRelation, TImplementation, TComponent, TPermission>(TRelation relation, TImplementation implementation)
    : ICollisionPairFilter where TRelation : struct, IRelation<TComponent, TComponent>
    where TImplementation : struct, IBitmapRelation<TRelation, TComponent, TPermission>
    where TComponent : struct where TPermission : struct, ICategoryPermission
{
    public QueryDescription AddReads(QueryDescription description) => description.Optional<TComponent>();
    public bool Matches(World world, Entity first, Entity second) => world.Has<TComponent>(first) && world.Has<TComponent>(second)
        && relation.Matches(in world.Read<TComponent>(first), in world.Read<TComponent>(second));
}
public readonly struct CollisionPairAnd<TFirst, TSecond>(TFirst first, TSecond second) : ICollisionPairFilter
    where TFirst : struct, ICollisionPairFilter where TSecond : struct, ICollisionPairFilter
{
    public QueryDescription AddReads(QueryDescription description) => second.AddReads(first.AddReads(description));
    public bool Matches(World world, Entity a, Entity b) => first.Matches(world, a, b) && second.Matches(world, a, b);
}
public readonly struct CollisionPairExcept<TFilter>(TFilter filter) : ICollisionPairFilter where TFilter : struct, ICollisionPairFilter
{
    public QueryDescription AddReads(QueryDescription description) => filter.AddReads(description);
    public bool Matches(World world, Entity first, Entity second) => !filter.Matches(world, first, second);
}
