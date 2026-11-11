using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Spatial;

public static class SpatialPairQueries
{
    public static void FillPairs<TEnumerable, TPartition>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        SpatialPairBuffer<TPartition> pairs)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct =>
        Fill(query, pairs, false, null);
    public static void FillPairs<TEnumerable, TPartition>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        ulong lastSystemVersion, SpatialPairBuffer<TPartition> pairs)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct =>
        Fill(query, pairs, false, lastSystemVersion);
    public static void FillPairsParallel<TEnumerable, TPartition>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        SpatialPairBuffer<TPartition> pairs)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct =>
        Fill(query, pairs, true, null);
    public static void FillPairsParallel<TEnumerable, TPartition>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        ulong lastSystemVersion, SpatialPairBuffer<TPartition> pairs)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct =>
        Fill(query, pairs, true, lastSystemVersion);
    private static void Fill<TEnumerable, TPartition>(RelationQuery<TEnumerable, (Entity, Entity)> query,
        SpatialPairBuffer<TPartition> pairs, bool parallel, ulong? since)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
    {
        var fill = new RelationQuery<PairFill<TEnumerable, TPartition>, (Entity, Entity)>(new(query.Enumerable, pairs, parallel, since));
        var output = new PairCount();
        if (since is { } version) fill.Enumerate(version, ref output);
        else fill.Enumerate(ref output);
    }
    private readonly struct PairFill<TEnumerable, TPartition>(TEnumerable source, SpatialPairBuffer<TPartition> pairs,
        bool parallel, ulong? since) : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public Query GetQuery(int position) => source.GetQuery(position);
        public QueryDescription AddReads(QueryDescription definition, int position) => source.AddReads(definition, position);
        public bool Enumerate<TOutput>(scoped RelationQueryViews queries, ref TOutput output)
            where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
        {
            pairs.Fill(source, queries, parallel, since);
            return true;
        }
    }
    internal struct PairCount : IQueryRelationOutput<(Entity, Entity)>
    {
        public int Count;
        public static bool UsesBoundColumns => false;
        public bool Add(in (Entity, Entity) pair) { Count++; return true; }
        public void Bind(QueryChunk first, QueryChunk second) { }
        public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed) => Add(in pair);
    }
}

/// <summary>Holds partition counts, output offsets and their completed entity pairs.</summary>
public sealed class SpatialPairBuffer<TPartition> : IDisposable where TPartition : struct
{
    private readonly Scheduler _scheduler;
    private readonly TPartition[] _partitions;
    private readonly int[] _counts, _offsets, _partitionIndices, _fillPartitions;
    private readonly JobResource _pairsResource;
    private (Entity, Entity)[] _pairs = [];
    public SpatialPairBuffer(Scheduler scheduler, int partitionCapacity)
    {
        _scheduler = scheduler;
        _partitions = new TPartition[partitionCapacity];
        _counts = new int[partitionCapacity];
        _offsets = new int[partitionCapacity + 1];
        _partitionIndices = new int[partitionCapacity];
        _fillPartitions = new int[partitionCapacity];
        _pairsResource = scheduler.CreateResource("Spatial pair output");
        for (int index = 0; index < partitionCapacity; index++) _partitionIndices[index] = index;
    }
    public int Count { get; private set; }
    public int PairCapacity => _pairs.Length;
    public int PartitionCapacity => _partitions.Length;
    public int PartitionCount { get; private set; }
    public int FilledPartitionCount { get; private set; }
    public ReadOnlySpan<(Entity, Entity)> Pairs => _pairs.AsSpan(0, Count);
    public JobResourceAccess FillAccess => JobResourceAccess.Write(_pairsResource);
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_pairsResource);
    internal void Fill<TEnumerable>(TEnumerable source, scoped RelationQueryViews queries, bool parallel, ulong? since)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition>
    {
        source.Prepare(queries);
        PartitionCount = source.CreatePartitions(_partitions);
        JobHandle access = source.World.Execution?.CurrentAccess ?? default;
        var work = new PartitionWork<TEnumerable>(source, this, since, false);
        if (parallel) _scheduler.ExecuteRanges(_partitionIndices, PartitionCount, work, access);
        else for (int index = 0; index < PartitionCount; index++) work.Execute(in _partitionIndices[index]);
        _offsets[0] = 0;
        int fillCount = 0;
        for (int index = 0; index < PartitionCount; index++)
        {
            _offsets[index + 1] = _offsets[index] + _counts[index];
            if (_counts[index] != 0) _fillPartitions[fillCount++] = index;
        }
        Count = _offsets[PartitionCount];
        if (_pairs.Length < Count) Array.Resize(ref _pairs, Math.Max(Count, _pairs.Length * 2));
        FilledPartitionCount = fillCount;
        work = new(source, this, since, true);
        if (parallel) _scheduler.ExecuteRanges(_fillPartitions, fillCount, work, access);
        else for (int index = 0; index < fillCount; index++) work.Execute(in _fillPartitions[index]);
    }
    private readonly struct PartitionWork<TEnumerable> : IRangeWork<int>
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition>
    {
        private readonly TEnumerable _source;
        private readonly SpatialPairBuffer<TPartition> _buffer;
        private readonly Query _access, _first, _second;
        private readonly bool _fill;
        internal PartitionWork(TEnumerable source, SpatialPairBuffer<TPartition> buffer, ulong? since, bool fill)
        {
            _source = source; _buffer = buffer; _fill = fill;
            _first = source.GetQuery(0); _second = source.QueryCount == 2 ? source.GetQuery(1) : default;
            if (since is { } version) { _first = _first.Since(version); if (source.QueryCount == 2) _second = _second.Since(version); }
            QueryDescription reads = source.AddReads(new QueryDescription(), 0);
            foreach (int type in _first.Description.Definition.AccessTypes) reads.OptionalReads.Add(type);
            if (source.QueryCount == 2)
            {
                reads = source.AddReads(reads, 1);
                foreach (int type in _second.Description.Definition.AccessTypes) reads.OptionalReads.Add(type);
            }
            _access = source.World.Query(reads);
        }
        public void Execute(in int index)
        {
            using QueryScope access = _access.OpenRead();
            using QueryScope first = _first.OpenRead();
            if (_source.QueryCount == 1) Execute(index, new(_source.World, access, first, default));
            else
            {
                using QueryScope second = _second.OpenRead();
                Execute(index, new(_source.World, access, first, second));
            }
        }
        private void Execute(int index, scoped RelationQueryViews queries)
        {
            if (_fill)
            {
                var output = new PairArrayOutput(_buffer._pairs.AsSpan(_buffer._offsets[index], _buffer._counts[index]));
                _source.Enumerate(_buffer._partitions[index], queries, ref output);
            }
            else
            {
                var output = new SpatialPairQueries.PairCount();
                _source.Enumerate(_buffer._partitions[index], queries, ref output);
                _buffer._counts[index] = output.Count;
            }
        }
    }
    private ref struct PairArrayOutput(Span<(Entity, Entity)> pairs) : IQueryRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _pairs = pairs;
        private int _count;
        public static bool UsesBoundColumns => false;
        public bool Add(in (Entity, Entity) pair) { _pairs[_count++] = pair; return true; }
        public void Bind(QueryChunk first, QueryChunk second) { }
        public bool Add(in (Entity, Entity) pair, int first, int second, bool reversed) => Add(in pair);
    }
    public void Dispose() => _scheduler.ReleaseResource(_pairsResource);
}
