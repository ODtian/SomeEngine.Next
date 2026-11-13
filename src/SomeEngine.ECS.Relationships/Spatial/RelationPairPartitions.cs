using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public readonly struct RelationPairPartition;

public static class SpatialPartitionQueries
{
    public static RelationQuery<RelationPairPartitions<TEnumerable>, (Entity, Entity)> PartitionPairs<TEnumerable>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query) where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)> =>
        new(new(query.Enumerable));
}

/// <summary>One typed partition of a general relation query.</summary>
public readonly struct RelationPairPartitions<TEnumerable>(TEnumerable source) : ISpatialPartitionEnumerable<RelationPairPartition>
    where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TEnumerable.ReadsFromSingleQuery;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription description, int position) => source.AddReads(description, position);
    public void Prepare(scoped RelationQueryViews views) { }
    public int CreatePartitions(Span<RelationPairPartition> partitions) { partitions[0] = default; return 1; }
    public bool Enumerate<TOutput>(scoped RelationQueryViews views, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct => source.Enumerate(views, ref output);
    public bool Enumerate<TOutput>(RelationPairPartition partition, scoped RelationQueryViews views, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct => source.Enumerate(views, ref output);
}
