using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public ref struct ComponentRelation<TRelation, TFirst, TSecond> : IRelation<(Entity, Entity)>
    where TRelation : struct, IRelation<TFirst, TSecond>, allows ref struct
    where TFirst : struct
    where TSecond : struct
{
    private readonly QueryCursor _query;
    private TRelation _relation;

    public ComponentRelation(QueryCursor query, TRelation relation)
    {
        _query = query;
        _relation = relation;
    }

    public bool Matches(in (Entity, Entity) pair)
    {
        if (!_query.TryGetRow(pair.Item1, out var first) || !first.Has<TFirst>() ||
            !_query.TryGetRow(pair.Item2, out var second) || !second.Has<TSecond>())
            return false;
        return _relation.Matches(in first.ReadRef<TFirst>(), in second.ReadRef<TSecond>());
    }
}
