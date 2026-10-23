using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public readonly struct EdgeComponentExcept<TEnumerable, TPayload, TRelation, T1, T2> : IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TPayload : struct, IComponent
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

    public QueryHandle GetQuery(int position) => _enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        RelationEdgeEndpoints<TPayload>.AddReads(_enumerable.AddReads(definition, position))
            .WithOptionalRead<T1>().WithOptionalRead<T2>();

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<TPayload>>, allows ref struct
    {
        var component = new ComponentRelation<TRelation, T1, T2>(queries.Access, _relation);
        var relation = new NegatedRelation<ComponentRelation<TRelation, T1, T2>, (Entity, Entity)>(component);
        var filtered = new EdgeRelationFilterOutput<TPayload,
            NegatedRelation<ComponentRelation<TRelation, T1, T2>, (Entity, Entity)>, TOutput>(
            queries.Access, relation, output);
        try
        {
            return _enumerable.Enumerate(queries, ref filtered);
        }
        finally
        {
            output = filtered.Output;
        }
    }
}
