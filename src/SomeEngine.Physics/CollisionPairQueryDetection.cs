using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Diagnostics;

namespace SomeEngine.Physics;

/// <summary>Reads the complete active query when geometry refers to additional simulated entities.</summary>
public interface ICollisionPairQueryDetection
{
    bool SupportsParallel => false;
    void BeginParallel(int partitions) { }
    void SelectPartition(int partition) { }
    QueryDefinition AddReads(QueryDefinition definition);
    void Execute(in CollisionGeometryPair pair, scoped QueryCursor access, scoped QueryRow first, scoped QueryRow second);
}

public static partial class CollisionPairQueries
{
    public static void DetectPairsWithQuery<TEnumerable, TPartition, TDetection>(
        this RelationQuery<TEnumerable, (Entity, Entity)> query, CollisionPairBins<TPartition> bins,
        int bin, TDetection detection, QueryReadResources? resources = null)
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        var detect = new RelationQuery<QueryBinDetection<TEnumerable, TPartition, TDetection>, (Entity, Entity)>(
            new(query.Enumerable, bins, bin, detection, resources));
        var output = new PairCount();
        detect.Enumerate(ref output);
    }

    /// <summary>Consumes a type bin in an already registered query borrow, including declared referenced resources.</summary>
    public static void DetectBinWithQuery<TPartition, TDetection>(scoped QueryCursor access,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection, QueryReadResources? resources = null)
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        using var trace = EngineTrace.Begin(CollisionTraceSites.NarrowPhase);
        EngineTrace.Counter(CollisionTraceSites.DetectionCount, bins.ReadBin(bin).Length);
        var work = new BinDetectionWork<TPartition, TDetection>(bins, bin, detection);
        if (resources is null) work.Execute(access);
        else resources.ExecuteEntities(access, new BinEntities(bins.ReadBin(bin)), ref work,
            static (scoped QueryCursor query, ref BinDetectionWork<TPartition, TDetection> state) => state.Execute(query));
    }

    private readonly ref struct BinEntities(ReadOnlySpan<CollisionGeometryPair> pairs) : IQueryReadEntities
    {
        private readonly ReadOnlySpan<CollisionGeometryPair> _pairs = pairs;
        public int Count => _pairs.Length * 2;
        public Entity GetEntity(int index) => (index & 1) == 0 ? _pairs[index / 2].First : _pairs[index / 2].Second;
    }

    private readonly struct BinDetectionWork<TPartition, TDetection>(CollisionPairBins<TPartition> bins,
        int bin, TDetection detection)
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        public void Execute(scoped QueryCursor access)
        {
            TDetection kernel = detection;
            const int pairsPerJob = 128;
            int count = bins.ReadBin(bin).Length;
            if (kernel.SupportsParallel && count > pairsPerJob)
            {
                int partitions = (count + pairsPerJob - 1) / pairsPerJob;
                kernel.BeginParallel(partitions);
                var ranges = new DetectPairRanges<TPartition, TDetection>(bins, bin, kernel, pairsPerJob);
                ReadOnlyQueryTripleJobs.ExecuteParallel(access, access, access, ranges, partitions, []);
                return;
            }
            if (kernel.SupportsParallel)
            {
                kernel.BeginParallel(1);
                kernel.SelectPartition(0);
            }
            foreach (ref readonly CollisionGeometryPair pair in bins.ReadBin(bin))
            {
                access.TryGetRow(pair.First, out QueryRow first);
                access.TryGetRow(pair.Second, out QueryRow second);
                kernel.Execute(in pair, access, first, second);
            }
        }
    }

    private readonly struct DetectPairRanges<TPartition, TDetection>(CollisionPairBins<TPartition> bins,
        int bin, TDetection detection, int pairsPerJob) : IReadOnlyQueryTripleJob
        where TPartition : struct where TDetection : struct, ICollisionPairQueryDetection
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor firstRows, scoped QueryCursor secondRows)
        {
            TDetection kernel = detection;
            kernel.SelectPartition(index);
            ReadOnlySpan<CollisionGeometryPair> pairs = bins.ReadBin(bin);
            int first = index * pairsPerJob, last = Math.Min(first + pairsPerJob, pairs.Length);
            for (int pairIndex = first; pairIndex < last; pairIndex++)
            {
                ref readonly CollisionGeometryPair pair = ref pairs[pairIndex];
                access.TryGetRow(pair.First, out QueryRow firstRow);
                access.TryGetRow(pair.Second, out QueryRow secondRow);
                kernel.Execute(in pair, access, firstRow, secondRow);
            }
        }
    }

    private readonly struct QueryBinDetection<TEnumerable, TPartition, TDetection>(TEnumerable source,
        CollisionPairBins<TPartition> bins, int bin, TDetection detection, QueryReadResources? resources)
        : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, IQueryRelationEnumerable<(Entity, Entity)>
        where TPartition : struct
        where TDetection : struct, ICollisionPairQueryDetection
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public static bool ReadsFromSingleQuery => false;
        public QueryHandle GetQuery(int position) => source.GetQuery(position);
        public QueryDefinition AddReads(QueryDefinition definition, int position) =>
            detection.AddReads(source.AddReads(definition, position));

        public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        {
            DetectBinWithQuery(queries.Access, bins, bin, detection, resources);
            return true;
        }
    }
}
