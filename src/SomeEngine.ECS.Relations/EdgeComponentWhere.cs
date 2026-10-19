using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct EdgeComponentWhere<TEnumerable, TPayload, TRelation, T1, T2> : IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TPayload : struct, IComponent
    where TRelation : struct, IRelation<T1, T2>
    where T1 : struct
    where T2 : struct
{
    private readonly TEnumerable _enumerable;
    private readonly TRelation _relation;

    public EdgeComponentWhere(TEnumerable enumerable, TRelation relation)
    {
        _enumerable = enumerable;
        _relation = relation;
    }

    public World World => _enumerable.World;

    public int QueryCount => _enumerable.QueryCount;

    public QueryHandle GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        RelationEdgeEndpoints<TPayload>.AddReads(_enumerable.AddReads(definition, position))
            .WithOptionalRead<T1>().WithOptionalRead<T2>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<TPayload>>, allows ref struct
    {
        var relation = new ComponentRelation<TRelation, T1, T2>(queries.Access, _relation);
        var filtered = new EdgeRelationFilterOutput<TPayload, ComponentRelation<TRelation, T1, T2>, TOutput>(
            queries.Access, relation, new(ref output));
        return _enumerable.Enumerate(queries, ref filtered);
    }
}
