using System.Buffers;
using SomeEngine.Job;

namespace SomeEngine.Render.Systems;

/// <summary>
/// Stable worker-partition grouping used by render queues and exact-layout instance composition.
/// The caller chooses the fixed partition count; there is no hidden small/large-work threshold.
/// Keys are ordered by their first source occurrence, and rows inside one key retain source order.
/// </summary>
internal sealed class StableGrouping<TKey>
    where TKey : notnull
{
    private readonly Partition[] _partitions;
    private readonly Dictionary<TKey, int> _groupIndices = [];
    private readonly List<TKey> _groupKeys = [];

    internal StableGrouping(int partitionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);
        _partitions = new Partition[partitionCount];
        for (int index = 0; index < partitionCount; index++)
            _partitions[index] = new Partition();
    }

    internal TKey GetKey(int group)
    {
        if ((uint)group >= (uint)_groupKeys.Count)
            throw new ArgumentOutOfRangeException(nameof(group));
        return _groupKeys[group];
    }

    internal int Group(
        TKey[] source,
        int sourceCount,
        int[] groupCounts,
        int[] groupStarts,
        StableGroupPlacement[] placements,
        JobScheduleOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(groupCounts);
        ArgumentNullException.ThrowIfNull(groupStarts);
        ArgumentNullException.ThrowIfNull(placements);
        if ((uint)sourceCount > (uint)source.Length)
            throw new ArgumentOutOfRangeException(nameof(sourceCount));
        if (groupCounts.Length < sourceCount
            || groupStarts.Length < sourceCount
            || placements.Length < sourceCount)
        {
            throw new ArgumentException(
                "Stable grouping outputs must have capacity for every source element.");
        }

        _groupIndices.Clear();
        _groupKeys.Clear();
        if (sourceCount == 0)
            return 0;

        int activePartitions = Math.Min(_partitions.Length, sourceCount);
        for (int index = 0; index < activePartitions; index++)
        {
            Partition partition = _partitions[index];
            partition.Keys.Clear();
            partition.Counts.Clear();
            partition.Start = (int)((long)sourceCount * index / activePartitions);
            partition.End = (int)((long)sourceCount * (index + 1) / activePartitions);
        }

        var countJob = new CountPartitionsJob(source, _partitions);
        JobResourceAccess[] countAccesses = RentAccesses(
            activePartitions,
            fixedCount: 1,
            twoPerPartition: true,
            out int countAccessCount);
        try
        {
            countAccesses[0] = JobResourceAccess.Read(source, 0, sourceCount);
            int output = 1;
            for (int index = 0; index < activePartitions; index++)
            {
                countAccesses[output++] = JobResourceAccess.Write(_partitions[index].Counts);
                countAccesses[output++] = JobResourceAccess.Write(_partitions[index].Keys);
            }
            JobHandle counted = JobSystem.ScheduleParallel(
                in countJob,
                activePartitions,
                batchSize: 1,
                countAccesses.AsSpan(0, countAccessCount),
                options);

            int[] groupCountResult = ArrayPool<int>.Shared.Rent(1);
            try
            {
                var mergeJob = new MergePartitionsJob(
                    _partitions,
                    activePartitions,
                    _groupIndices,
                    _groupKeys,
                    groupCounts,
                    groupStarts,
                    groupCountResult);
                JobResourceAccess[] mergeAccesses = RentAccesses(
                    activePartitions,
                    fixedCount: 5,
                    twoPerPartition: true,
                    out int mergeAccessCount);
                try
                {
                    mergeAccesses[0] = JobResourceAccess.Write(_groupIndices);
                    mergeAccesses[1] = JobResourceAccess.Write(_groupKeys);
                    mergeAccesses[2] = JobResourceAccess.Write(groupCounts, 0, sourceCount);
                    mergeAccesses[3] = JobResourceAccess.Write(groupStarts, 0, sourceCount);
                    mergeAccesses[4] = JobResourceAccess.Write(groupCountResult, 0, 1);
                    int mergeAccessIndex = 5;
                    for (int index = 0; index < activePartitions; index++)
                    {
                        mergeAccesses[mergeAccessIndex++] = JobResourceAccess.Write(_partitions[index].Counts);
                        mergeAccesses[mergeAccessIndex++] = JobResourceAccess.Read(_partitions[index].Keys);
                    }
                    JobHandle merged = JobSystem.Schedule(
                        in mergeJob,
                        mergeAccesses.AsSpan(0, mergeAccessCount),
                        options,
                        counted);

                    var scatterJob = new ScatterPlacementsJob(
                        source,
                        _partitions,
                        _groupIndices,
                        placements);
                    JobResourceAccess[] scatterAccesses = RentAccesses(
                        activePartitions,
                        fixedCount: 3,
                        twoPerPartition: false,
                        out int scatterAccessCount);
                    try
                    {
                        scatterAccesses[0] = JobResourceAccess.Read(source, 0, sourceCount);
                        scatterAccesses[1] = JobResourceAccess.Read(_groupIndices);
                        scatterAccesses[2] = JobResourceAccess.Write(placements, 0, sourceCount);
                        for (int index = 0; index < activePartitions; index++)
                        {
                            scatterAccesses[3 + index] =
                                JobResourceAccess.Write(_partitions[index].Counts);
                        }
                        JobSystem.ScheduleParallel(
                            in scatterJob,
                            activePartitions,
                            batchSize: 1,
                            scatterAccesses.AsSpan(0, scatterAccessCount),
                            options,
                            merged).Complete();
                    }
                    finally
                    {
                        ReturnAccesses(scatterAccesses);
                    }
                }
                finally
                {
                    ReturnAccesses(mergeAccesses);
                }

                return groupCountResult[0];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(groupCountResult);
            }
        }
        finally
        {
            ReturnAccesses(countAccesses);
        }
    }

    private static JobResourceAccess[] RentAccesses(
        int activePartitions,
        int fixedCount,
        bool twoPerPartition,
        out int count)
    {
        count = checked(fixedCount + activePartitions * (twoPerPartition ? 2 : 1));
        return ArrayPool<JobResourceAccess>.Shared.Rent(count);
    }

    private static void ReturnAccesses(JobResourceAccess[] accesses)
    {
        accesses.AsSpan().Clear();
        ArrayPool<JobResourceAccess>.Shared.Return(accesses);
    }

    private sealed class Partition
    {
        internal int Start;
        internal int End;
        internal Dictionary<TKey, int> Counts { get; } = [];
        internal List<TKey> Keys { get; } = [];
    }

    private readonly struct CountPartitionsJob(
        TKey[] source,
        Partition[] partitions) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Partition partition = partitions[index];
            for (int sourceIndex = partition.Start; sourceIndex < partition.End; sourceIndex++)
            {
                TKey key = source[sourceIndex];
                if (partition.Counts.TryGetValue(key, out int count))
                {
                    partition.Counts[key] = checked(count + 1);
                }
                else
                {
                    partition.Counts.Add(key, 1);
                    partition.Keys.Add(key);
                }
            }
        }
    }

    private readonly struct MergePartitionsJob(
        Partition[] partitions,
        int activePartitions,
        Dictionary<TKey, int> groupIndices,
        List<TKey> groupKeys,
        int[] groupCounts,
        int[] groupStarts,
        int[] groupCountResult) : IJob
    {
        public void Execute()
        {
            for (int partitionIndex = 0; partitionIndex < activePartitions; partitionIndex++)
            {
                Partition partition = partitions[partitionIndex];
                for (int keyIndex = 0; keyIndex < partition.Keys.Count; keyIndex++)
                {
                    TKey key = partition.Keys[keyIndex];
                    int count = partition.Counts[key];
                    if (!groupIndices.TryGetValue(key, out int group))
                    {
                        group = groupKeys.Count;
                        groupIndices.Add(key, group);
                        groupKeys.Add(key);
                        groupCounts[group] = count;
                    }
                    else
                    {
                        groupCounts[group] = checked(groupCounts[group] + count);
                    }
                }
            }

            int start = 0;
            for (int group = 0; group < groupKeys.Count; group++)
            {
                groupStarts[group] = start;
                start = checked(start + groupCounts[group]);
            }

            int[] nextRows = ArrayPool<int>.Shared.Rent(groupKeys.Count);
            try
            {
                nextRows.AsSpan(0, groupKeys.Count).Clear();
                for (int partitionIndex = 0; partitionIndex < activePartitions; partitionIndex++)
                {
                    Partition partition = partitions[partitionIndex];
                    for (int keyIndex = 0; keyIndex < partition.Keys.Count; keyIndex++)
                    {
                        TKey key = partition.Keys[keyIndex];
                        int group = groupIndices[key];
                        int localCount = partition.Counts[key];
                        partition.Counts[key] = nextRows[group];
                        nextRows[group] = checked(nextRows[group] + localCount);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(nextRows);
            }

            groupCountResult[0] = groupKeys.Count;
        }
    }

    private readonly struct ScatterPlacementsJob(
        TKey[] source,
        Partition[] partitions,
        Dictionary<TKey, int> groupIndices,
        StableGroupPlacement[] placements) : IJobParallelFor
    {
        public void Execute(int index)
        {
            Partition partition = partitions[index];
            for (int sourceIndex = partition.Start; sourceIndex < partition.End; sourceIndex++)
            {
                TKey key = source[sourceIndex];
                int row = partition.Counts[key];
                partition.Counts[key] = checked(row + 1);
                placements[sourceIndex] = new StableGroupPlacement(
                    groupIndices[key],
                    row);
            }
        }
    }
}

internal readonly record struct StableGroupPlacement(int Group, int Row);
