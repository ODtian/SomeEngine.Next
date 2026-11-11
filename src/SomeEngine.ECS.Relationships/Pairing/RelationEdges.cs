using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static partial class RelationQueries
{
    public static RelationQuery<OutgoingRelationEdges<T>, RelationEdge<T>> OutgoingEdges<T>(this World world, Entity source)
        where T : struct => new(new OutgoingRelationEdges<T>(world, source));

    public static RelationQuery<IncomingRelationEdges<T>, RelationEdge<T>> IncomingEdges<T>(this World world, Entity target)
        where T : struct => new(new IncomingRelationEdges<T>(world, target));

    public static RelationQuery<IncidentRelationEdges<T>, RelationEdge<T>> IncidentEdges<T>(this World world, Entity endpoint)
        where T : struct => new(new IncidentRelationEdges<T>(world, endpoint));

    public static RelationQuery<RelationEdgesBetween<T>, RelationEdge<T>> EdgesBetween<T>(this World world, Entity first, Entity second)
        where T : struct => new(new RelationEdgesBetween<T>(world, first, second));

    public static RelationQuery<SingleRelationEdge<T>, RelationEdge<T>> Edge<T>(this World world, RelationEdge<T> edge)
        where T : struct => new(new SingleRelationEdge<T>(world, edge));
}

public readonly struct OutgoingRelationEdges<T>(World world, Entity source) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct
{
    public World World => world;

    public int QueryCount => 0;

    public Query GetQuery(int position) => default;

    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<T>>, allows ref struct
    {
        using var relations = Relations<T>.Outgoing(world, source);
        foreach (var entry in relations.Entries)
        {
            var edge = new RelationEdge<T>(entry.Edge);
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct IncomingRelationEdges<T>(World world, Entity target) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct
{
    public World World => world;

    public int QueryCount => 0;

    public Query GetQuery(int position) => default;

    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<T>>, allows ref struct
    {
        using var relations = Relations<T>.Incoming(world, target);
        foreach (var entry in relations.Entries)
        {
            var edge = new RelationEdge<T>(entry.Edge);
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct RelationEdgesBetween<T>(World world, Entity first, Entity second) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct
{
    public World World => world;

    public int QueryCount => 0;

    public Query GetQuery(int position) => default;

    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<T>>, allows ref struct
    {
        if (RelationSchema.For<T>().Direction == RelationDirection.Directed)
        {
            using var relations = Relations<T>.Outgoing(world, first);
            foreach (var entry in relations.Entries)
            {
                var edge = new RelationEdge<T>(entry.Edge);
                if (entry.Other == second && !output.Add(in edge)) return false;
            }
        }
        else
        {
            using var relations = Relations<T>.Incident(world, first);
            foreach (var entry in relations)
            {
                var edge = new RelationEdge<T>(entry.Edge);
                if (entry.Other == second && !output.Add(in edge)) return false;
            }
        }
        return true;
    }
}

public readonly struct IncidentRelationEdges<T>(World world, Entity endpoint) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct
{
    public World World => world;

    public int QueryCount => 0;

    public Query GetQuery(int position) => default;

    public QueryDescription AddReads(QueryDescription definition, int position) => definition;

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<T>>, allows ref struct
    {
        using var relations = Relations<T>.Incident(world, endpoint);
        foreach (var entry in relations)
        {
            var edge = new RelationEdge<T>(entry.Edge);
            if (!output.Add(in edge))
                return false;
        }
        return true;
    }
}

public readonly struct SingleRelationEdge<T>(World world, RelationEdge<T> edge) : IQueryRelationEnumerable<RelationEdge<T>>
    where T : struct
{
    public World World => world;

    public int QueryCount => 0;

    public Query GetQuery(int position) => default;

    public QueryDescription AddReads(QueryDescription definition, int position) => definition.Optional<RelationEndpoints<T>>();

    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<RelationEdge<T>>, allows ref struct =>
        !world.IsAlive(edge.Entity) || !world.Has<RelationEndpoints<T>>(edge.Entity) || output.Add(in edge);
}
