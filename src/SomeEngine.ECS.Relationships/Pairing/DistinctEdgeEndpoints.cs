using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<DistinctEdgeEndpoints<TEnumerable, TPayload>, (Entity, Entity)> DistinctEndpoints<TEnumerable, TPayload>(
        this RelationQuery<TEnumerable, RelationEdge<TPayload>> source)
        where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
        where TPayload : struct => new(new DistinctEdgeEndpoints<TEnumerable, TPayload>(source.Enumerable));

    public static RelationQuery<DistinctEdgeEndpoints<TEnumerable, TPayload>, (Entity, Entity)> DistinctUnorderedEndpoints<TEnumerable, TPayload>(
        this RelationQuery<TEnumerable, RelationEdge<TPayload>> source)
        where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
        where TPayload : struct => new(new DistinctEdgeEndpoints<TEnumerable, TPayload>(source.Enumerable, unordered: true));
}

public readonly struct DistinctEdgeEndpoints<TEnumerable, TPayload>(TEnumerable enumerable, bool unordered = false)
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TPayload : struct
{
    public World World => enumerable.World;

    public int QueryCount => enumerable.QueryCount;

    public Query GetQuery(int position) => enumerable.GetQuery(position);

    public QueryDescription AddReads(QueryDescription definition, int position) =>
        RelationEdgeEndpoints<TPayload>.AddReads(enumerable.AddReads(definition, position));

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var projected = new EdgeEndpointOutput<TPayload, TOutput>(queries.World, unordered, new(ref output));
        try { return enumerable.Enumerate(queries, ref projected); }
        finally { projected.Dispose(); }
    }
}

internal ref struct EdgeEndpointOutput<TPayload, TOutput> : IQueryRelationOutput<RelationEdge<TPayload>>
    where TPayload : struct
    where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
{
    private ReadLookup<RelationEndpoints<TPayload>> _endpoints;
    private readonly bool _unordered;
    private readonly HashSet<(Entity, Entity)> _pairs;

    internal EdgeEndpointOutput(World world, bool unordered, RelationOutputReference<TOutput> output)
    {
        _endpoints = world.OpenRead<RelationEndpoints<TPayload>>();
        _unordered = unordered;
        _pairs = new HashSet<(Entity, Entity)>();
        _output = output;
    }

    private readonly RelationOutputReference<TOutput> _output;

    public bool Add(in RelationEdge<TPayload> edge)
    {
        var pair = RelationEdgeEndpoints<TPayload>.Read(ref _endpoints, in edge);
        if (_unordered)
            pair = EntityOrder.OrderPair(pair.Item1, pair.Item2);
        return !_pairs.Add(pair) || _output.Value.Add(in pair);
    }

    public void Dispose() => _endpoints.Dispose();
    public void Bind(QueryChunk first, QueryChunk second) { }
    public bool Add(in RelationEdge<TPayload> edge, int first, int second, bool reversed) => Add(in edge);
}
