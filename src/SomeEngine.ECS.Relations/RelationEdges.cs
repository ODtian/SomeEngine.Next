using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<OutgoingRelationEdges<T>, RelationEdge<T>> OutgoingEdges<T>(this World world, Entity source)
        where T : struct, IComponent => new(new OutgoingRelationEdges<T>(world, source));

    public static RelationQuery<IncomingRelationEdges<T>, RelationEdge<T>> IncomingEdges<T>(this World world, Entity target)
        where T : struct, IComponent => new(new IncomingRelationEdges<T>(world, target));

    public static RelationQuery<IncidentRelationEdges<T>, RelationEdge<T>> IncidentEdges<T>(this World world, Entity endpoint)
        where T : struct, IComponent => new(new IncidentRelationEdges<T>(world, endpoint));

    public static RelationQuery<RelationEdgesBetween<T>, RelationEdge<T>> EdgesBetween<T>(this World world, Entity first, Entity second)
        where T : struct, IComponent => new(new RelationEdgesBetween<T>(world, first, second));

    public static RelationQuery<SingleRelationEdge<T>, RelationEdge<T>> Edge<T>(this World world, RelationEdge<T> edge)
        where T : struct, IComponent => new(new SingleRelationEdge<T>(world, edge));
}

public readonly struct OutgoingRelationEdges<T>(World world, Entity source) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct, IComponent
{
    public World World => world;

    public int QueryCount => 0;

    public QueryHandle GetQuery(int position) => default;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<T>>, allows ref struct
    {
        foreach (var entry in world.GetOutgoingRelations<T>(source).Entries)
        {
            var edge = entry.Edge;
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct IncomingRelationEdges<T>(World world, Entity target) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct, IComponent
{
    public World World => world;

    public int QueryCount => 0;

    public QueryHandle GetQuery(int position) => default;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<T>>, allows ref struct
    {
        foreach (var entry in world.GetIncomingRelations<T>(target).Entries)
        {
            var edge = entry.Edge;
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct RelationEdgesBetween<T>(World world, Entity first, Entity second) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct, IComponent
{
    public World World => world;

    public int QueryCount => 0;

    public QueryHandle GetQuery(int position) => default;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<T>>, allows ref struct
    {
        foreach (var edge in world.GetRelationEdgesBetween<T>(first, second))
        {
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct IncidentRelationEdges<T>(World world, Entity endpoint) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct, IComponent
{
    public World World => world;

    public int QueryCount => 0;

    public QueryHandle GetQuery(int position) => default;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<T>>, allows ref struct
    {
        foreach (var entry in world.GetIncidentRelations<T>(endpoint).Entries)
        {
            var edge = entry.Edge;
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct SingleRelationEdge<T>(World world, RelationEdge<T> edge) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct, IComponent
{
    public World World => world;

    public int QueryCount => 0;

    public QueryHandle GetQuery(int position) => default;

    public QueryDefinition AddReads(QueryDefinition definition, int position) => definition;

    public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IRelationOutput<RelationEdge<T>>, allows ref struct =>
        !queries.Access.TryGetRow(edge.Entity, out _) || output.Add(in edge);
}
