using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public readonly struct EdgeComponentExcept<TEnumerable, TPayload, TRelation, T1, T2> : IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TPayload : struct
    where TRelation : struct, IRelation<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private readonly TEnumerable _enumerable;
    private readonly TRelation _relation;

    public EdgeComponentExcept(TEnumerable enumerable, TRelation relation)
    {
        _enumerable = enumerable;
        _relation = relation;
    }

    public World World => _enumerable.World;

    public int QueryCount => _enumerable.QueryCount;

    public Query GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        RelationEdgeEndpoints<TPayload>.AddReads(_enumerable.AddReads(definition, position))
            .Optional<T1>().Optional<T2>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<TPayload>>, allows ref struct
    {
        using var component = new ComponentRelation<TRelation, T1, T2>(queries.World, _relation);
        var relation = new NegatedRelation<ComponentRelation<TRelation, T1, T2>, (Entity, Entity)>(component);
        var filtered = new EdgeRelationFilterOutput<TPayload,
            NegatedRelation<ComponentRelation<TRelation, T1, T2>, (Entity, Entity)>, TOutput>(
            queries.World, relation, new(ref output));
        try { return _enumerable.Enumerate(queries, ref filtered); }
        finally { filtered.Dispose(); }
    }
}
