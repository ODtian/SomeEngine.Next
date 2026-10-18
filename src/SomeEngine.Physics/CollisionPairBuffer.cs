using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public static class CollisionPairQueries
{
    public static void FillPairs<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBuffer pairs)
        where TEnumerable : struct, ICollisionPartitionEnumerable => Fill(query, pairs, false, null);

    public static void FillPairs<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        uint lastSystemVersion, CollisionPairBuffer pairs)
        where TEnumerable : struct, ICollisionPartitionEnumerable => Fill(query, pairs, false, lastSystemVersion);

    public static void FillPairsParallel<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBuffer pairs)
        where TEnumerable : struct, ICollisionPartitionEnumerable => Fill(query, pairs, true, null);

    public static void FillPairsParallel<TEnumerable>(this RelationQuery<TEnumerable, (Entity, Entity)> query,
        uint lastSystemVersion, CollisionPairBuffer pairs)
        where TEnumerable : struct, ICollisionPartitionEnumerable => Fill(query, pairs, true, lastSystemVersion);

    private static void Fill<TEnumerable>(RelationQuery<TEnumerable, (Entity, Entity)> query,
        CollisionPairBuffer pairs, bool parallel, uint? lastSystemVersion)
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        var fill = new RelationQuery<PairFill<TEnumerable>, (Entity, Entity)>(new(query.Enumerable, pairs, parallel));
        var output = new PairCount();
        if (lastSystemVersion is { } baseline)
            fill.Enumerate(baseline, ref output);
        else
            fill.Enumerate(ref output);
    }

    private readonly struct PairFill<TEnumerable>(TEnumerable source, CollisionPairBuffer pairs, bool parallel)
        : IQueryRelationEnumerable<(Entity, Entity)>
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        public World World => source.World;
        public int QueryCount => source.QueryCount;
        public QueryHandle GetQuery(int position) => source.GetQuery(position);
        public QueryDefinition AddReads(QueryDefinition definition, int position) => source.AddReads(definition, position);

        public bool Enumerate<TOutput>(RelationQueryViews queries, ref TOutput output)
            where TOutput : struct, IRelationOutput<(Entity, Entity)>, allows ref struct
        {
            pairs.Fill(source, queries, parallel);
            return true;
        }
    }

    internal struct PairCount : IRelationOutput<(Entity, Entity)>
    {
        internal int Count;
        public bool Add(in (Entity, Entity) pair)
        {
            Count++;
            return true;
        }
    }
}

/// <summary>Persistent partition data and an output range sized from the actual relation count.</summary>
public sealed class CollisionPairBuffer : IDisposable
{
    private readonly Scheduler _scheduler;
    private readonly CollisionNodePair[] _partitions;
    private readonly int[] _counts;
    private readonly int[] _offsets;
    private readonly int[] _fillPartitions;
    private readonly JobResourceAccess[] _countAccesses;
    private readonly JobResourceAccess[] _fillAccesses;
    private readonly JobResourceToken _queryResource;
    private readonly JobResourceToken _countResource;
    private readonly JobResourceToken _offsetResource;
    private readonly JobResourceToken _pairResource;
    private (Entity, Entity)[] _pairs = [];

    public CollisionPairBuffer(Scheduler scheduler, int partitionCapacity)
    {
        _scheduler = scheduler;
        _partitions = new CollisionNodePair[partitionCapacity];
        _counts = new int[partitionCapacity];
        _offsets = new int[partitionCapacity + 1];
        _fillPartitions = new int[partitionCapacity];
        _countAccesses = new JobResourceAccess[partitionCapacity];
        _fillAccesses = new JobResourceAccess[2 * partitionCapacity];
        _queryResource = scheduler.CreateResourceToken("Collision pair buffer");
        _countResource = scheduler.CreateResourceToken("Collision partition counts");
        _offsetResource = scheduler.CreateResourceToken("Collision output offsets");
        _pairResource = scheduler.CreateResourceToken("Collision pair output");
    }

    public int Count { get; private set; }
    public int PairCapacity => _pairs.Length;
    public int PartitionCapacity => _partitions.Length;
    public int PartitionCount { get; private set; }
    public int FilledPartitionCount { get; private set; }
    public ReadOnlySpan<(Entity, Entity)> Pairs => _pairs.AsSpan(0, Count);
    public JobResourceAccess FillAccess => JobResourceAccess.Write(_queryResource);
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_pairResource);

    internal void Fill<TEnumerable>(TEnumerable source, RelationQueryViews queries, bool parallel)
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        source.Prepare(queries);
        int partitionCount = source.Tree.CreatePartitions(_partitions);
        PartitionCount = partitionCount;
        for (int index = 0; index < partitionCount; index++)
            _countAccesses[index] = JobResourceAccess.Write(_countResource, index, 1);
        var countJob = new CountJob<TEnumerable>(source, this);
        JobHandle counted = default;
        if (parallel)
            counted = ReadOnlyQueryPairJobs.ExecuteParallel(queries.Access, queries.First, in countJob,
                partitionCount, _countAccesses.AsSpan(0, partitionCount), 1);
        else
            ReadOnlyQueryPairJobs.ExecuteInline(queries.Access, queries.First, in countJob, partitionCount);

        var prefixJob = new PrefixJob(this, partitionCount);
        JobHandle prefixed = default;
        if (parallel)
        {
            ReadOnlySpan<JobResourceAccess> accesses = [JobResourceAccess.Read(_countResource),
                JobResourceAccess.Write(_offsetResource), JobResourceAccess.Write(_pairResource)];
            prefixed = _scheduler.Schedule(prefixJob, accesses, counted);
            prefixed.Complete();
        }
        else
            prefixJob.Execute();

        int fillCount = 0;
        for (int index = 0; index < partitionCount; index++)
        {
            int count = _offsets[index + 1] - _offsets[index];
            if (count == 0)
                continue;
            _fillPartitions[fillCount] = index;
            _fillAccesses[2 * fillCount] = JobResourceAccess.Read(_offsetResource, index, 2);
            _fillAccesses[2 * fillCount + 1] = JobResourceAccess.Write(_pairResource, _offsets[index], count);
            fillCount++;
        }
        var fillJob = new FillJob<TEnumerable>(source, this);
        FilledPartitionCount = fillCount;
        if (parallel)
            ReadOnlyQueryPairJobs.ExecuteParallel(queries.Access, queries.First, in fillJob,
                fillCount, _fillAccesses.AsSpan(0, 2 * fillCount), 2, prefixed);
        else
            ReadOnlyQueryPairJobs.ExecuteInline(queries.Access, queries.First, in fillJob, fillCount);
    }

    private readonly struct CountJob<TEnumerable>(TEnumerable source, CollisionPairBuffer buffer) : IReadOnlyQueryPairJob
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        public void Execute(int index, QueryCursor access, QueryCursor members)
        {
            var output = new CollisionPairQueries.PairCount();
            source.Enumerate(buffer._partitions[index], new RelationQueryViews(access, members, default), ref output);
            buffer._counts[index] = output.Count;
        }
    }

    private readonly struct PrefixJob(CollisionPairBuffer buffer, int partitionCount) : IJob
    {
        public void Execute()
        {
            buffer._offsets[0] = 0;
            for (int index = 0; index < partitionCount; index++)
                buffer._offsets[index + 1] = checked(buffer._offsets[index] + buffer._counts[index]);
            buffer.Count = buffer._offsets[partitionCount];
            if (buffer._pairs.Length < buffer.Count)
                Array.Resize(ref buffer._pairs, Math.Max(buffer.Count, buffer._pairs.Length * 2));
        }
    }

    private readonly struct FillJob<TEnumerable>(TEnumerable source, CollisionPairBuffer buffer) : IReadOnlyQueryPairJob
        where TEnumerable : struct, ICollisionPartitionEnumerable
    {
        public void Execute(int index, QueryCursor access, QueryCursor members)
        {
            int partition = buffer._fillPartitions[index];
            int start = buffer._offsets[partition];
            int count = buffer._offsets[partition + 1] - start;
            var output = new PairArrayOutput(buffer._pairs.AsSpan(start, count));
            source.Enumerate(buffer._partitions[partition], new RelationQueryViews(access, members, default), ref output);
        }
    }

    private ref struct PairArrayOutput(Span<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _pairs = pairs;
        private int _count;

        public bool Add(in (Entity, Entity) pair)
        {
            _pairs[_count++] = pair;
            return true;
        }
    }

    public void Dispose()
    {
        _scheduler.ReleaseResourceToken(_queryResource);
        _scheduler.ReleaseResourceToken(_countResource);
        _scheduler.ReleaseResourceToken(_offsetResource);
        _scheduler.ReleaseResourceToken(_pairResource);
    }
}
