using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

internal static class RelationEdgeEndpoints<T>
    where T : struct, IComponent
{
    internal static QueryDefinition AddReads(QueryDefinition definition) =>
        RelationSchema.For<T>().Direction == RelationDirection.Directed
            ? definition.WithOptionalRead<DirectedRelationEndpoints<T>>()
            : definition.WithOptionalRead<UndirectedRelationEndpoints<T>>();

    internal static (Entity, Entity) Read(QueryCursor query, in RelationEdge<T> edge)
    {
        query.TryGetRow(edge.Entity, out var row);
        if (RelationSchema.For<T>().Direction == RelationDirection.Directed)
        {
            ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<T>>();
            return (endpoints.Source, endpoints.Target);
        }
        else
        {
            ref readonly var endpoints = ref row.ReadRef<UndirectedRelationEndpoints<T>>();
            return (endpoints.EndpointA, endpoints.EndpointB);
        }
    }
}

internal ref struct EdgeRelationFilterOutput<TPayload, TRelation, TOutput> : IRelationOutput<RelationEdge<TPayload>>
    where TPayload : struct, IComponent
    where TRelation : struct, IRelation<(Entity, Entity)>, allows ref struct
    where TOutput : struct, IRelationOutput<RelationEdge<TPayload>>, allows ref struct
{
    private readonly QueryCursor _query;
    private TRelation _relation;

    internal EdgeRelationFilterOutput(QueryCursor query, TRelation relation, TOutput output)
    {
        _query = query;
        _relation = relation;
        Output = output;
    }

    internal TOutput Output;

    public bool Add(in RelationEdge<TPayload> edge)
    {
        var pair = RelationEdgeEndpoints<TPayload>.Read(_query, in edge);
        return !_relation.Matches(in pair) || Output.Add(in edge);
    }
}
