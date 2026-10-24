using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<DistinctEdgeEndpoints<TEnumerable, TPayload>, (Entity, Entity)> DistinctEndpoints<TEnumerable, TPayload>(
        this RelationQuery<TEnumerable, RelationEdge<TPayload>> source)
        where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
        where TPayload : struct, IComponent => new(new DistinctEdgeEndpoints<TEnumerable, TPayload>(source.Enumerable));

    public static RelationQuery<DistinctEdgeEndpoints<TEnumerable, TPayload>, (Entity, Entity)> DistinctUnorderedEndpoints<TEnumerable, TPayload>(
        this RelationQuery<TEnumerable, RelationEdge<TPayload>> source)
        where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
        where TPayload : struct, IComponent => new(new DistinctEdgeEndpoints<TEnumerable, TPayload>(source.Enumerable, unordered: true));
}

public readonly struct DistinctEdgeEndpoints<TEnumerable, TPayload>(TEnumerable enumerable, bool unordered = false)
    : IQueryRelationEnumerable<(Entity, Entity)>
    where TEnumerable : struct, IQueryRelationEnumerable<RelationEdge<TPayload>>
    where TPayload : struct, IComponent
{
    public World World => enumerable.World;

    public int QueryCount => enumerable.QueryCount;

    public QueryHandle GetQuery(int position) => enumerable.GetQuery(position);

    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        RelationEdgeEndpoints<TPayload>.AddReads(enumerable.AddReads(definition, position));

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        var projected = new EdgeEndpointOutput<TPayload, TOutput>(queries.Access, unordered, new(ref output));
        return enumerable.Enumerate(queries, ref projected);
    }
}

internal ref struct EdgeEndpointOutput<TPayload, TOutput> : IQueryRelationOutput<RelationEdge<TPayload>>
    where TPayload : struct, IComponent
    where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
{
    private readonly QueryCursor _query;
    private readonly bool _unordered;
    private readonly HashSet<(Entity, Entity)> _pairs;

    internal EdgeEndpointOutput(QueryCursor query, bool unordered, RelationOutputReference<TOutput> output)
    {
        _query = query;
        _unordered = unordered;
        _pairs = new HashSet<(Entity, Entity)>();
        _output = output;
    }

    private readonly RelationOutputReference<TOutput> _output;

    public bool Add(in RelationEdge<TPayload> edge)
    {
        var pair = RelationEdgeEndpoints<TPayload>.Read(_query, in edge);
        if (_unordered)
            pair = EntityOrder.OrderPair(pair.Item1, pair.Item2);
        return !_pairs.Add(pair) || _output.Value.Add(in pair);
    }

    public bool Add(in RelationEdge<TPayload> edge, scoped QueryRow first, scoped QueryRow second) => Add(in edge);
}
