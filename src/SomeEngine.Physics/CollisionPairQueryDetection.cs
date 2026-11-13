using System.Buffers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Diagnostics;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>One concrete narrow phase reading its declared components by actual entity identity.</summary>
public interface ICollisionPairQueryDetection
{
    bool SupportsParallel => false;
    void BeginParallel(int partitions) { }
    void SelectPartition(int partition) { }
    QueryDescription AddReads(QueryDescription description);
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, Query query, List<JobResourceAccess> accesses) => PhysicsQueryAccess.AddAccesses(world, query, accesses);
    void Execute(in CollisionGeometryPair pair, World world);
}
public static partial class CollisionPairQueries
{
    public static void DetectPairsWithQuery<TEnumerable, TPartition, TDetection>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins, int bin, TDetection detection)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)> where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        var detect = new RelationQuery<QueryBinDetection<TEnumerable, TPartition, TDetection>, (Entity, Entity)>(new(query.Enumerable, bins, bin, detection));
        var output = new PairCounter(); detect.Enumerate(ref output);
    }
    public static void DetectBinWithQuery<TPartition, TDetection>(World world,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection)
        where TPartition : struct where TDetection : struct, ICollisionPairQueryDetection
    {
        using var trace = EngineTrace.Begin(CollisionTraceSites.NarrowPhase);
        int count = bins.ReadBin(bin).Length;
        EngineTrace.Counter(CollisionTraceSites.DetectionCount, count);
        const int pairsPerRange = 128;
        TDetection kernel = detection;
        if (kernel.SupportsParallel && count > pairsPerRange)
        {
            int countRanges = (count + pairsPerRange - 1) / pairsPerRange;
            kernel.BeginParallel(countRanges);
            DetectionRange[] ranges = ArrayPool<DetectionRange>.Shared.Rent(countRanges);
            try
            {
                for (int index = 0; index < countRanges; index++) ranges[index] = new(index, index * pairsPerRange, Math.Min(pairsPerRange, count - index * pairsPerRange));
                var work = new DetectRange<TPartition, TDetection>(world, bins, bin, kernel);
                world.Execution!.ExecuteRanges(ranges, countRanges, in work);
            }
            finally { ArrayPool<DetectionRange>.Shared.Return(ranges); }
        }
        else
        {
            if (kernel.SupportsParallel) { kernel.BeginParallel(1); kernel.SelectPartition(0); }
            foreach (ref readonly CollisionGeometryPair pair in bins.ReadBin(bin)) kernel.Execute(in pair, world);
        }
    }
    private readonly record struct DetectionRange(int Partition, int Start, int Count);
    private readonly struct DetectRange<TPartition, TDetection>(World world, CollisionPairBins<TPartition> bins, int bin, TDetection detection) : IRangeWork<DetectionRange>
        where TPartition : struct where TDetection : struct, ICollisionPairQueryDetection
    {
        public void Execute(in DetectionRange range)
        {
            TDetection kernel = detection; kernel.SelectPartition(range.Partition);
            foreach (ref readonly CollisionGeometryPair pair in bins.ReadBin(bin).Slice(range.Start, range.Count)) kernel.Execute(in pair, world);
        }
    }
    private readonly struct QueryBinDetection<TEnumerable, TPartition, TDetection>(TEnumerable source,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection) : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)> where TPartition : struct where TDetection : struct, ICollisionPairQueryDetection
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => false;
        public Query GetQuery(int position) => source.GetQuery(position);
        public QueryDescription AddReads(QueryDescription description, int position) => detection.AddReads(source.AddReads(description, position));
        public bool Enumerate<TOutput>(scoped RelationQueryViews views, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        { DetectBinWithQuery(views.World, bins, bin, detection); return true; }
    }
}
