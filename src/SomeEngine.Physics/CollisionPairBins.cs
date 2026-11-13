using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct CollisionPairType(int Bin, int FirstGeometry, int SecondGeometry);
public readonly record struct CollisionGeometryPair(Entity First, Entity Second, int FirstGeometry, int SecondGeometry);
public interface ICollisionPairClassifier
{
    QueryDescription AddReads(QueryDescription description);
    CollisionPairType Classify(World world, Entity first, Entity second);
}
public interface ICollisionPairDetection
{
    QueryDescription AddReads(QueryDescription description);
    void Execute(in CollisionGeometryPair pair, World world);
}

/// <summary>Counts each native candidate partition, prefixes its type bins, and writes final pairs directly.</summary>
public sealed class CollisionPairBins<TPartition> : IDisposable where TPartition : struct
{
    private readonly Scheduler _scheduler;
    private readonly JobResource _resource;
    private readonly TPartition[] _partitions;
    private readonly int[] _indices, _counts, _positions, _offsets;
    private CollisionGeometryPair[] _pairs = [];
    public CollisionPairBins(Scheduler scheduler, int partitionCapacity, int binCount)
    {
        _scheduler = scheduler; _resource = scheduler.CreateResource("Collision type bins");
        _partitions = new TPartition[partitionCapacity]; _indices = new int[partitionCapacity];
        _counts = new int[partitionCapacity * binCount]; _positions = new int[_counts.Length]; _offsets = new int[binCount + 1];
        for (int i = 0; i < partitionCapacity; i++) _indices[i] = i;
    }
    public int BinCount => _offsets.Length - 1;
    public int Count { get; private set; }
    public JobResourceAccess FillAccess => JobResourceAccess.Write(_resource);
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    public ReadOnlySpan<CollisionGeometryPair> ReadBin(int bin) => _pairs.AsSpan(_offsets[bin], _offsets[bin + 1] - _offsets[bin]);
    internal ReadOnlySpan<CollisionGeometryPair> ReadPairs() => _pairs.AsSpan(0, Count);
    internal void Fill<TEnumerable, TClassifier>(TEnumerable source, TClassifier classifier, scoped RelationQueryViews views, bool parallel)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TClassifier : struct, ICollisionPairClassifier
    {
        source.Prepare(views);
        int count = source.CreatePartitions(_partitions); Array.Clear(_counts, 0, count * BinCount);
        var work = new FillPartitions<TEnumerable, TClassifier>(source, classifier, this, false);
        if (parallel && source.World.Execution is { } execution) execution.ExecuteRanges(_indices, count, work);
        else if (parallel) _scheduler.ExecuteRanges(_indices, count, work, default);
        else for (int i = 0; i < count; i++) work.Execute(in _indices[i]);
        int position = 0;
        for (int bin = 0; bin < BinCount; bin++)
        {
            _offsets[bin] = position;
            for (int partition = 0; partition < count; partition++)
            { int index = partition * BinCount + bin; _positions[index] = position; position += _counts[index]; }
        }
        _offsets[BinCount] = Count = position;
        if (_pairs.Length < position) Array.Resize(ref _pairs, Math.Max(position, _pairs.Length * 2));
        work = new(source, classifier, this, true);
        if (parallel && source.World.Execution is { } writeExecution) writeExecution.ExecuteRanges(_indices, count, work);
        else if (parallel) _scheduler.ExecuteRanges(_indices, count, work, default);
        else for (int i = 0; i < count; i++) work.Execute(in _indices[i]);
    }
    private readonly struct FillPartitions<TEnumerable, TClassifier> : IRangeWork<int>
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TClassifier : struct, ICollisionPairClassifier
    {
        private readonly TEnumerable _source;
        private readonly TClassifier _classifier;
        private readonly CollisionPairBins<TPartition> _bins;
        private readonly bool _write;
        private readonly Query _access, _first, _second;
        internal FillPartitions(TEnumerable source, TClassifier classifier, CollisionPairBins<TPartition> bins, bool write)
        {
            _source = source; _classifier = classifier; _bins = bins; _write = write;
            QueryDescription reads = classifier.AddReads(source.AddReads(new QueryDescription(), 0));
            if (source.QueryCount > 1) reads = source.AddReads(reads, 1);
            _access = source.World.Query(reads); _first = source.GetQuery(0);
            _second = source.QueryCount > 1 ? source.GetQuery(1) : default;
        }
        public void Execute(in int partition)
        {
            using QueryScope access = _access.OpenRead();
            using QueryScope first = _first.OpenRead();
            if (_source.QueryCount == 1) Fill(partition, new(_source.World, access, first, default));
            else { using QueryScope second = _second.OpenRead(); Fill(partition, new(_source.World, access, first, second)); }
        }
        private void Fill(int partition, scoped RelationQueryViews views)
        {
            if (_write)
            {
                var output = new WriteOutput<TClassifier>(_source.World, _classifier,
                    _bins._positions.AsSpan(partition * _bins.BinCount, _bins.BinCount), _bins._pairs);
                _source.Enumerate(_bins._partitions[partition], views, ref output);
            }
            else
            {
                var output = new CountOutput<TClassifier>(_source.World, _classifier,
                    _bins._counts.AsSpan(partition * _bins.BinCount, _bins.BinCount));
                _source.Enumerate(_bins._partitions[partition], views, ref output);
            }
        }
    }
    private ref struct CountOutput<TClassifier>(World world, TClassifier classifier, Span<int> counts) : IQueryRelationOutput<(Entity, Entity)>
        where TClassifier : struct, ICollisionPairClassifier
    {
        private readonly Span<int> _counts = counts;
        public static bool UsesBoundColumns => false;
        public void Bind(QueryChunk first, QueryChunk second) { }
        public bool Add(in (Entity, Entity) pair) { _counts[classifier.Classify(world, pair.Item1, pair.Item2).Bin]++; return true; }
        public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed) => Add(in pair);
    }
    private ref struct WriteOutput<TClassifier>(World world, TClassifier classifier, Span<int> positions,
        CollisionGeometryPair[] pairs) : IQueryRelationOutput<(Entity, Entity)> where TClassifier : struct, ICollisionPairClassifier
    {
        private readonly Span<int> _positions = positions;
        public static bool UsesBoundColumns => false;
        public void Bind(QueryChunk first, QueryChunk second) { }
        public bool Add(in (Entity, Entity) pair)
        { var type = classifier.Classify(world, pair.Item1, pair.Item2); pairs[_positions[type.Bin]++] = new(pair.Item1, pair.Item2, type.FirstGeometry, type.SecondGeometry); return true; }
        public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed) => Add(in pair);
    }
    public void Dispose() => _scheduler.ReleaseResource(_resource);
}
