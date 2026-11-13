using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public sealed partial class CollisionIntervalIndex<TSettings> where TSettings : struct, ICollisionIntervalSettings
{
    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsWithin, WithinSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsBetween, BetweenSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityProduct2, ProductSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityProduct2, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsWithin, WithinSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsBetween, BetweenSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityProduct2, ProductSpatialPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityProduct2, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));
}

public readonly struct IntervalCandidates<TGeometry, TBoundsReader, TSettings, TSource, TPairing>(
    TSource source, CollisionIntervalIndex<TSettings> intervals, TBoundsReader boundsReader)
    : ISpatialPartitionEnumerable<IntervalPairRange>
    where TGeometry : struct
    where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
    where TSettings : struct, ICollisionIntervalSettings
    where TSource : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, ISpatialPairing
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription definition, int position) =>
        source.AddReads(definition, position).Optional<TGeometry>();

    public void Prepare(scoped RelationQueryViews queries)
    {
        intervals.BeginUpdate();
        ReadMembers(queries.First);
        if (QueryCount == 2)
            ReadMembers(queries.Second);
        intervals.EndUpdate();
    }

    private void ReadMembers(scoped QueryScope members)
    {
        foreach (QueryChunk chunk in members)
        {
            var geometry = chunk.Read<TGeometry>();
            for (int row = 0; row < chunk.Count; row++)
            {
                SpatialBounds spatial = boundsReader.Read(in geometry[row]);
                var bounds = new CollisionBounds(spatial.Min, spatial.Max);
                intervals.Update(chunk.Entities[row], in bounds);
            }
        }
    }

    public int CreatePartitions(Span<IntervalPairRange> partitions) => intervals.CreatePartitions(partitions);
    public bool Enumerate<TOutput>(IntervalPairRange partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        intervals.Enumerate<TPairing, TOutput>(partition, queries, ref output);
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        Prepare(queries);
        return intervals.Enumerate<TPairing, TOutput>(intervals.AllEntries, queries, ref output);
    }
}
