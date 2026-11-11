using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Spatial;

public sealed partial class SpatialGrid<TSettings> where TSettings : struct, ISpatialGridSettings
{
    public RelationQuery<GridCandidates<SpatialBounds, SpatialBoundsReader, TSettings, EntityPairsWithin, WithinSpatialPairing>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs) =>
        Candidates<SpatialBounds, SpatialBoundsReader>(pairs, default);

    public RelationQuery<GridCandidates<TGeometry, TBoundsReader, TSettings, EntityPairsWithin, WithinSpatialPairing>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, TBoundsReader reader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> => new(new(pairs.Enumerable, this, reader));

    public RelationQuery<GridCandidates<SpatialBounds, SpatialBoundsReader, TSettings, EntityPairsBetween, BetweenSpatialPairing>, (Entity, Entity)>
        Candidates(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs) =>
        Candidates<SpatialBounds, SpatialBoundsReader>(pairs, default);

    public RelationQuery<GridCandidates<TGeometry, TBoundsReader, TSettings, EntityPairsBetween, BetweenSpatialPairing>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs, TBoundsReader reader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> => new(new(pairs.Enumerable, this, reader));

    public RelationQuery<GridCandidates<SpatialBounds, SpatialBoundsReader, TSettings, EntityProduct2, ProductSpatialPairing>, (Entity, Entity)>
        Candidates(RelationQuery<EntityProduct2, (Entity, Entity)> pairs) =>
        Candidates<SpatialBounds, SpatialBoundsReader>(pairs, default);

    public RelationQuery<GridCandidates<TGeometry, TBoundsReader, TSettings, EntityProduct2, ProductSpatialPairing>, (Entity, Entity)>
        Candidates<TGeometry, TBoundsReader>(RelationQuery<EntityProduct2, (Entity, Entity)> pairs, TBoundsReader reader)
        where TGeometry : struct
        where TBoundsReader : struct, ISpatialBoundsReader<TGeometry> => new(new(pairs.Enumerable, this, reader));

}

public readonly struct GridCandidates<TGeometry, TBoundsReader, TSettings, TSource, TPairing>(
    TSource source, SpatialGrid<TSettings> grid, TBoundsReader boundsReader)
    : ISpatialPartitionEnumerable<GridPairRange>
    where TGeometry : struct
    where TBoundsReader : struct, ISpatialBoundsReader<TGeometry>
    where TSettings : struct, ISpatialGridSettings
    where TSource : struct, IQueryRelationEnumerable<(Entity, Entity)>
    where TPairing : struct, ISpatialPairing
{
    public World World => source.World;
    public int QueryCount => source.QueryCount;
    public static bool ReadsFromSingleQuery => TSource.ReadsFromSingleQuery;
    public Query GetQuery(int position) => source.GetQuery(position);
    public QueryDescription AddReads(QueryDescription description, int position) =>
        source.AddReads(description, position).Optional<TGeometry>();
    public void Prepare(scoped RelationQueryViews queries)
    {
        grid.BeginUpdate();
        ReadMembers(queries.First);
        if (QueryCount == 2) ReadMembers(queries.Second);
        grid.EndUpdate();
    }
    private void ReadMembers(scoped QueryScope members)
    {
        foreach (QueryChunk chunk in members)
        {
            if (!chunk.TryRead<TGeometry>(out ReadOnlySpan<TGeometry> values)) continue;
            for (int row = 0; row < chunk.Count; row++)
                if (chunk.Has<TGeometry>(row)) grid.Update(chunk.Entities[row], boundsReader.Read(in values[row]));
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
