using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

internal static class RelationEdgeEndpoints<T> where T : struct
{
    internal static QueryDescription AddReads(QueryDescription definition) => definition.Optional<RelationEndpoints<T>>();
    internal static (Entity, Entity) Read(ref ReadLookup<RelationEndpoints<T>> values, in RelationEdge<T> edge)
    {
        ref readonly RelationEndpoints<T> endpoints = ref values.Read(edge.Entity);
        return (endpoints.Source, endpoints.Target);
    }
}

internal ref struct EdgeRelationFilterOutput<TPayload, TRelation, TOutput> : IQueryRelationOutput<RelationEdge<TPayload>>
    where TPayload : struct
    where TRelation : struct, IRelation<(Entity, Entity)>, allows ref struct
    where TOutput : struct, IQueryRelationOutput<RelationEdge<TPayload>>, allows ref struct
{
    private ReadLookup<RelationEndpoints<TPayload>> _endpoints;
    private TRelation _relation;
    private readonly RelationOutputReference<TOutput> _output;
    internal EdgeRelationFilterOutput(World world, TRelation relation, RelationOutputReference<TOutput> output)
    {
        _endpoints = world.OpenRead<RelationEndpoints<TPayload>>();
        _relation = relation; _output = output;
    }
    public void Dispose() => _endpoints.Dispose();
    public void Bind(QueryChunk first, QueryChunk second) { }
    public bool Add(in RelationEdge<TPayload> edge, int first, int second, bool reversed) => Add(in edge);
    public bool Add(in RelationEdge<TPayload> edge)
    {
        var pair = RelationEdgeEndpoints<TPayload>.Read(ref _endpoints, in edge);
        return !_relation.Matches(in pair) || _output.Value.Add(in edge);
    }
}
