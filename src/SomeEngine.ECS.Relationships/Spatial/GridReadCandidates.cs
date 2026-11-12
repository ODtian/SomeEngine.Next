using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public sealed partial class SpatialGrid<TSettings> where TSettings : struct, ISpatialGridSettings
{
    public RelationQuery<GridReadCandidates<TSettings, EntityPairsWithin, WithinSpatialPairing>, (Entity, Entity)>
        ReadCandidates(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs) => new(new(pairs.Enumerable, this));

    public RelationQuery<GridReadCandidates<TSettings, EntityPairsBetween, BetweenSpatialPairing>, (Entity, Entity)>
        ReadCandidates(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs) => new(new(pairs.Enumerable, this));

    public RelationQuery<GridReadCandidates<TSettings, EntityProduct2, ProductSpatialPairing>, (Entity, Entity)>
        ReadCandidates(RelationQuery<EntityProduct2, (Entity, Entity)> pairs) => new(new(pairs.Enumerable, this));

}

/// <summary>Reads an explicitly maintained grid while retaining the participating query roles.</summary>
public readonly struct GridReadCandidates<TSettings, TSource, TPairing>(TSource source, SpatialGrid<TSettings> grid)
    : ISpatialPartitionEnumerable<GridPairRange>
    where TSettings : struct, ISpatialGridSettings
    where TSource : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, ISpatialPairing
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription definition, int position) => source.AddReads(definition, position);
    public void Prepare(scoped RelationQueryViews queries) { }
    public int CreatePartitions(Span<GridPairRange> partitions) => grid.CreatePartitions(partitions);
    public bool Enumerate<TOutput>(GridPairRange partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        grid.Enumerate<TPairing, TOutput>(partition, queries, ref output);
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        grid.Enumerate<TPairing, TOutput>(grid.AllMembers, queries, ref output);
}
