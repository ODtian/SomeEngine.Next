using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public interface ICollisionPairing
{
    static abstract bool IncludesSelf { get; }
    static abstract bool Emit<TOutput>(in (Entity, Entity) pair, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct;
}

public readonly struct WithinCollisionPairing : ICollisionPairing
{
    public static bool IncludesSelf => false;
    public static bool Emit<TOutput>(in (Entity, Entity) pair, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct => output.Add(in pair);
}

public readonly struct BetweenCollisionPairing : ICollisionPairing
{
    public static bool IncludesSelf => false;
    public static bool Emit<TOutput>(in (Entity, Entity) pair, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        !(queries.First.Contains(pair.Item1) && queries.Second.Contains(pair.Item2) ||
            queries.First.Contains(pair.Item2) && queries.Second.Contains(pair.Item1)) || output.Add(in pair);
}

public readonly struct ProductCollisionPairing : ICollisionPairing
{
    public static bool IncludesSelf => true;
    public static bool Emit<TOutput>(in (Entity, Entity) pair, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        if (queries.First.Contains(pair.Item1) && queries.Second.Contains(pair.Item2) && !output.Add(in pair))
            return false;
        if (pair.Item1 != pair.Item2 && queries.First.Contains(pair.Item2) && queries.Second.Contains(pair.Item1))
        {
            var reverse = (pair.Item2, pair.Item1);
            return output.Add(in reverse);
        }
        return true;
    }
}
