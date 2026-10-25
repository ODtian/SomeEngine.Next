using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public sealed partial class CollisionIntervalIndex<TSettings> where TSettings : struct, ICollisionIntervalSettings
{
    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsWithin, WithinCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsBetween, BetweenCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityProduct2, ProductCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityProduct2, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsWithin, WithinCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsBetween, BetweenCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<IntervalCandidates<TGeometry, TBoundsReader, TSettings,
        EntityProduct2, ProductCollisionPairing>, IntervalPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityProduct2, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));
}

public readonly struct IntervalCandidates<TGeometry, TBoundsReader, TSettings, TSource, TPairing>(
    TSource source, CollisionIntervalIndex<TSettings> intervals, TBoundsReader boundsReader)
    : ICollisionPartitionEnumerable<IntervalPairRange>
    where TGeometry : struct
    where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
    where TSettings : struct, ICollisionIntervalSettings
    where TSource : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, ICollisionPairing
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public QueryHandle GetQuery(int position) => source.GetQuery(position);
    public QueryDefinition AddReads(QueryDefinition definition, int position) =>
        source.AddReads(definition, position).WithOptionalRead<TGeometry>();

    public void Prepare(scoped RelationQueryViews queries)
    {
        intervals.BeginUpdate();
        ReadMembers(queries.First, queries.Access);
        if (QueryCount == 2)
            ReadMembers(queries.Second, queries.Access);
        intervals.EndUpdate();
    }

    private void ReadMembers(scoped QueryCursor members, scoped QueryCursor access)
    {
        foreach (var member in members.Rows)
        {
            access.TryGetRow(member.Entity, out QueryRow row);
            CollisionBounds bounds = boundsReader.Read(in row.ReadRef<TGeometry>());
            intervals.Update(member.Entity, in bounds);
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
