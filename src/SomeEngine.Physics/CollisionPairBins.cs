using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The selected detection bin and the geometry identity on each participating entity.</summary>
public readonly record struct CollisionPairType(int Bin, int FirstGeometry, int SecondGeometry);

public readonly record struct CollisionGeometryPair(Entity First, Entity Second, int FirstGeometry, int SecondGeometry);

/// <summary>Reads the project's geometry components to select one statically processed detection bin.</summary>
public interface ICollisionPairClassifier
{
    QueryDefinition AddReads(QueryDefinition definition);
    CollisionPairType Classify(scoped QueryRow first, scoped QueryRow second);
}

/// <summary>A concrete detection kernel for one bin, reading geometry from the current component rows.</summary>
public interface ICollisionPairDetection
{
    QueryDefinition AddReads(QueryDefinition definition);
    void Execute(in CollisionGeometryPair pair, scoped QueryRow first, scoped QueryRow second);
}

/// <summary>Reusable candidate partitions, type counts and direct writes to the final detection bins.</summary>
public sealed class CollisionPairBins<TPartition> : IDisposable where TPartition : struct
{
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;
    private readonly TPartition[] _partitions;
    private readonly int[] _counts;
    private readonly int[] _writePositions;
    private readonly int[] _binOffsets;
    private readonly JobResourceAccess[] _countAccesses;
    private readonly JobResourceAccess[] _writeAccesses;
    private CollisionGeometryPair[] _pairs = [];

    public CollisionPairBins(Scheduler scheduler, int partitionCapacity, int binCount)
    {
        _scheduler = scheduler;
        _resource = scheduler.CreateResourceToken("Collision type bins");
        _partitions = new TPartition[partitionCapacity];
        _counts = new int[partitionCapacity * binCount];
        _writePositions = new int[_counts.Length];
        _binOffsets = new int[binCount + 1];
        _countAccesses = new JobResourceAccess[partitionCapacity];
        _writeAccesses = new JobResourceAccess[_counts.Length];
    }

    public int BinCount => _binOffsets.Length - 1;
    public int Count { get; private set; }
    public JobResourceAccess FillAccess => JobResourceAccess.Write(_resource);
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);

    public ReadOnlySpan<CollisionGeometryPair> ReadBin(int bin) =>
        _pairs.AsSpan(_binOffsets[bin], _binOffsets[bin + 1] - _binOffsets[bin]);

    internal ReadOnlySpan<CollisionGeometryPair> ReadPairs() => _pairs.AsSpan(0, Count);

    internal void Fill<TEnumerable, TClassifier>(TEnumerable source, TClassifier classifier,
        scoped RelationQueryViews queries, bool parallel)
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TClassifier : struct, ICollisionPairClassifier
    {
        source.Prepare(queries);
        int partitionCount = source.CreatePartitions(_partitions);
        Array.Clear(_counts, 0, partitionCount * BinCount);
        for (int partition = 0; partition < partitionCount; partition++)
            _countAccesses[partition] = JobResourceAccess.Write(_resource, partition * BinCount, BinCount);
        var count = new CountPairs<TEnumerable, TClassifier>(source, classifier, this);
        Execute(queries, source.QueryCount, count, partitionCount, _countAccesses.AsSpan(0, partitionCount), parallel);

        int position = 0;
        int writeAccessCount = 0;
        for (int bin = 0; bin < BinCount; bin++)
        {
            _binOffsets[bin] = position;
            for (int partition = 0; partition < partitionCount; partition++)
            {
                int index = partition * BinCount + bin;
                _writePositions[index] = position;
                int length = _counts[index];
                if (length != 0)
                    _writeAccesses[writeAccessCount++] = JobResourceAccess.Write(_resource,
                        _counts.Length + position, length);
                position += length;
            }
        }
        _binOffsets[BinCount] = position;
        Count = position;
        if (_pairs.Length < Count)
            Array.Resize(ref _pairs, Math.Max(Count, _pairs.Length * 2));

        var write = new WritePairs<TEnumerable, TClassifier>(source, classifier, this);
        Execute(queries, source.QueryCount, write, partitionCount, _writeAccesses.AsSpan(0, writeAccessCount), parallel);
    }

    private static void Execute<TJob>(scoped RelationQueryViews queries, int queryCount, in TJob job, int count,
        ReadOnlySpan<JobResourceAccess> accesses, bool parallel)
        where TJob : struct, IReadOnlyQueryTripleJob
    {
        QueryCursor second = queryCount == 1 ? queries.First : queries.Second;
        if (parallel)
            ReadOnlyQueryTripleJobs.ExecuteParallel(queries.Access, queries.First, second,
                in job, count, accesses);
        else
            ReadOnlyQueryTripleJobs.ExecuteInline(queries.Access, queries.First, second, in job, count);
    }

    private readonly struct CountPairs<TEnumerable, TClassifier>(TEnumerable source, TClassifier classifier,
        CollisionPairBins<TPartition> bins) : IReadOnlyQueryTripleJob
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TClassifier : struct, ICollisionPairClassifier
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor first, scoped QueryCursor second)
        {
            var output = new CountOutput<TClassifier>(classifier, access,
                bins._counts.AsSpan(index * bins.BinCount, bins.BinCount));
            source.Enumerate(bins._partitions[index], new RelationQueryViews(access, first, second), ref output);
        }
    }

    private ref struct CountOutput<TClassifier>(TClassifier classifier, QueryCursor access, Span<int> counts)
        : IQueryRelationOutput<(Entity, Entity)>
        where TClassifier : struct, ICollisionPairClassifier
    {
        private readonly QueryCursor _access = access;
        private readonly Span<int> _counts = counts;

        public bool Add(in (Entity, Entity) pair)
        {
            _access.TryGetRow(pair.Item1, out QueryRow first);
            _access.TryGetRow(pair.Item2, out QueryRow second);
            _counts[classifier.Classify(first, second).Bin]++;
            return true;
        }
        public bool Add(in (Entity, Entity) pair, scoped QueryRow first, scoped QueryRow second) => Add(in pair);
    }

    private readonly struct WritePairs<TEnumerable, TClassifier>(TEnumerable source, TClassifier classifier,
        CollisionPairBins<TPartition> bins) : IReadOnlyQueryTripleJob
        where TEnumerable : struct, ICollisionPartitionEnumerable<TPartition>
        where TClassifier : struct, ICollisionPairClassifier
    {
        public void Execute(int index, scoped QueryCursor access, scoped QueryCursor first, scoped QueryCursor second)
        {
            var output = new WriteOutput<TClassifier>(classifier, access,
                bins._writePositions.AsSpan(index * bins.BinCount, bins.BinCount), bins._pairs);
            source.Enumerate(bins._partitions[index], new RelationQueryViews(access, first, second), ref output);
        }
    }

    private ref struct WriteOutput<TClassifier>(TClassifier classifier, QueryCursor access,
        Span<int> positions, CollisionGeometryPair[] pairs) : IQueryRelationOutput<(Entity, Entity)>
        where TClassifier : struct, ICollisionPairClassifier
    {
        private readonly QueryCursor _access = access;
        private readonly Span<int> _positions = positions;

        public bool Add(in (Entity, Entity) pair)
        {
            _access.TryGetRow(pair.Item1, out QueryRow first);
            _access.TryGetRow(pair.Item2, out QueryRow second);
            CollisionPairType type = classifier.Classify(first, second);
            pairs[_positions[type.Bin]++] = new(pair.Item1, pair.Item2, type.FirstGeometry, type.SecondGeometry);
            return true;
        }
        public bool Add(in (Entity, Entity) pair, scoped QueryRow first, scoped QueryRow second) => Add(in pair);
    }

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
}
