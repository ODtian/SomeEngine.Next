using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

public sealed partial class CollisionGrid<TSettings> where TSettings : struct, ICollisionGridSettings
{
    public RelationQuery<CollisionPairCandidates<GridCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsWithin, WithinCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<GridCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityPairsBetween, BetweenCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<GridCandidates<CollisionBounds, CollisionBoundsReader, TSettings,
        EntityProduct2, ProductCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates(RelationQuery<EntityProduct2, (Entity, Entity)> pairs) =>
        Candidates<CollisionBounds, CollisionBoundsReader>(pairs, default);

    public RelationQuery<CollisionPairCandidates<GridCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsWithin, WithinCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<GridCandidates<TGeometry, TBoundsReader, TSettings,
        EntityPairsBetween, BetweenCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));

    public RelationQuery<CollisionPairCandidates<GridCandidates<TGeometry, TBoundsReader, TSettings,
        EntityProduct2, ProductCollisionPairing>, GridPairRange, AllCollisionPairs>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityProduct2, (Entity, Entity)> pairs,
            TBoundsReader boundsReader)
        where TGeometry : struct
        where TBoundsReader : struct, ICollisionBoundsReader<TGeometry> =>
        new(new(new(pairs.Enumerable, this, boundsReader), default));
}

public readonly struct GridCandidates<TGeometry, TBoundsReader, TSettings, TSource, TPairing>(
    TSource source, CollisionGrid<TSettings> grid, TBoundsReader boundsReader)
    : ICollisionPartitionEnumerable<GridPairRange>
    where TGeometry : struct
    where TBoundsReader : struct, ICollisionBoundsReader<TGeometry>
    where TSettings : struct, ICollisionGridSettings
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
        grid.BeginUpdate();
        ReadMembers(queries.First, queries.Access);
        if (QueryCount == 2)
            ReadMembers(queries.Second, queries.Access);
        grid.EndUpdate();
    }

    private void ReadMembers(scoped QueryCursor members, scoped QueryCursor access)
    {
        foreach (var member in members.Rows)
        {
            access.TryGetRow(member.Entity, out QueryRow row);
            CollisionBounds bounds = boundsReader.Read(in row.ReadRef<TGeometry>());
            grid.Update(member.Entity, in bounds);
        }
    }

    public int CreatePartitions(Span<GridPairRange> partitions) => grid.CreatePartitions(partitions);
    public bool Enumerate<TOutput>(GridPairRange partition, scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct =>
        grid.Enumerate<TPairing, TOutput>(partition, queries, ref output);
    public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        Prepare(queries);
        return grid.Enumerate<TPairing, TOutput>(grid.AllMembers, queries, ref output);
    }
}
