using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct IntervalPairRange(int Start, int Count);

/// <summary>Entity bounds ordered by a selected axis for shared interval scans.</summary>
public sealed partial class CollisionIntervalIndex<TSettings>(TSettings settings)
    where TSettings : struct, ICollisionIntervalSettings
{
    private readonly List<CollisionInterval> _intervals = [];
    private readonly Dictionary<Entity, int> _entityIndices = [];
    private readonly List<IntervalPartition> _partitions = [];
    private readonly Dictionary<int, IntervalPartition> _partitionsByIndex = [];
    private readonly Stack<IntervalPartition> _emptyPartitions = [];
    private int _update;
    private int _entryCount;

    public TSettings Settings => settings;
    public JobResourceAccess GetWriteAccess(Scheduler scheduler) => JobResourceAccess.Write(scheduler, _entityIndices);

    internal void BeginUpdate() => _update++;

    internal void Update(Entity entity, in CollisionBounds bounds)
    {
        float minimum = ReadAxis(bounds.Min);
        float maximum = ReadAxis(bounds.Max);
        var interval = new CollisionInterval(entity, bounds, minimum, maximum, settings.GetPartition(minimum),
            settings.GetPartition(maximum), _update);
        if (_entityIndices.TryGetValue(entity, out int index))
            _intervals[index] = interval;
        else
        {
            _entityIndices.Add(entity, _intervals.Count);
            _intervals.Add(interval);
        }
    }

    internal void EndUpdate()
    {
        for (int index = _intervals.Count - 1; index >= 0; index--)
        {
            CollisionInterval interval = _intervals[index];
            if (interval.SeenUpdate == _update)
                continue;
            _entityIndices.Remove(interval.Entity);
            _intervals[index] = _intervals[^1];
            _intervals.RemoveAt(_intervals.Count - 1);
        }
        _intervals.Sort(IntervalOrder.Instance);
        for (int index = 0; index < _intervals.Count; index++)
            _entityIndices[_intervals[index].Entity] = index;
        UpdatePartitions();
    }

    internal int CreatePartitions(Span<IntervalPairRange> partitions)
    {
        int count = Math.Min(partitions.Length, _entryCount);
        for (int index = 0; index < count; index++)
        {
            int start = (int)((long)index * _entryCount / count);
            int end = (int)((long)(index + 1) * _entryCount / count);
            partitions[index] = new(start, end - start);
        }
        return count;
    }

    internal bool Enumerate<TPairing, TOutput>(IntervalPairRange partition, scoped RelationQueryViews queries,
        ref TOutput output)
        where TPairing : struct, ICollisionPairing
        where TOutput : struct, IQueryRelationOutput<(Entity, Entity)>, allows ref struct
    {
        ReadOnlySpan<CollisionInterval> intervals = CollectionsMarshal.AsSpan(_intervals);
        int end = partition.Start + partition.Count;
        for (int partitionIndex = FindPartition(partition.Start);
            partitionIndex < _partitions.Count && _partitions[partitionIndex].Start < end; partitionIndex++)
        {
            IntervalPartition members = _partitions[partitionIndex];
            ReadOnlySpan<int> indices = CollectionsMarshal.AsSpan(members.Intervals);
            int firstStart = Math.Max(0, partition.Start - members.Start);
            int firstEnd = Math.Min(indices.Length, end - members.Start);
            for (int firstIndex = firstStart; firstIndex < firstEnd; firstIndex++)
            {
                ref readonly CollisionInterval first = ref intervals[indices[firstIndex]];
                if (TPairing.IncludesSelf && members.Index == first.FirstPartition)
                {
                    var self = (first.Entity, first.Entity);
                    if (!TPairing.Emit(in self, queries, ref output))
                        return false;
                }
                for (int secondIndex = firstIndex + 1;
                    secondIndex < indices.Length && intervals[indices[secondIndex]].Minimum <= first.Maximum; secondIndex++)
                {
                    ref readonly CollisionInterval second = ref intervals[indices[secondIndex]];
                    if (members.Index != Math.Max(first.FirstPartition, second.FirstPartition) ||
                        !first.Bounds.Overlaps(in second.Bounds))
                        continue;
                    var pair = EntityOrder.OrderPair(first.Entity, second.Entity);
                    if (!TPairing.Emit(in pair, queries, ref output))
                        return false;
                }
            }
        }
        return true;
    }

    internal IntervalPairRange AllEntries => new(0, _entryCount);

    private void UpdatePartitions()
    {
        foreach (IntervalPartition partition in _partitions)
            partition.Intervals.Clear();
        for (int index = 0; index < _intervals.Count; index++)
        {
            CollisionInterval interval = _intervals[index];
            for (int key = interval.FirstPartition; key <= interval.LastPartition; key++)
            {
                if (!_partitionsByIndex.TryGetValue(key, out IntervalPartition? partition))
                {
                    partition = _emptyPartitions.TryPop(out IntervalPartition? reused) ? reused : new();
                    partition.Index = key;
                    _partitionsByIndex.Add(key, partition);
                    _partitions.Add(partition);
                }
                partition.Intervals.Add(index);
            }
        }
        for (int index = _partitions.Count - 1; index >= 0; index--)
        {
            IntervalPartition partition = _partitions[index];
            if (partition.Intervals.Count != 0)
                continue;
            _partitionsByIndex.Remove(partition.Index);
            _partitions[index] = _partitions[^1];
            _partitions.RemoveAt(_partitions.Count - 1);
            _emptyPartitions.Push(partition);
        }
        _partitions.Sort(PartitionOrder.Instance);
        _entryCount = 0;
        foreach (IntervalPartition partition in _partitions)
        {
            partition.Start = _entryCount;
            _entryCount += partition.Intervals.Count;
        }
    }

    private int FindPartition(int entry)
    {
        int first = 0, last = _partitions.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            IntervalPartition partition = _partitions[middle];
            if (partition.Start + partition.Intervals.Count <= entry)
                first = middle + 1;
            else
                last = middle;
        }
        return first;
    }

    private float ReadAxis(System.Numerics.Vector3 value) => settings.Axis switch
    {
        CollisionAxis.X => value.X,
        CollisionAxis.Y => value.Y,
        _ => value.Z,
    };

    private readonly struct CollisionInterval(Entity entity, CollisionBounds bounds, float minimum,
        float maximum, int firstPartition, int lastPartition, int seenUpdate)
    {
        internal readonly Entity Entity = entity;
        internal readonly CollisionBounds Bounds = bounds;
        internal readonly float Minimum = minimum;
        internal readonly float Maximum = maximum;
        internal readonly int FirstPartition = firstPartition;
        internal readonly int LastPartition = lastPartition;
        internal readonly int SeenUpdate = seenUpdate;
    }

    private sealed class IntervalPartition
    {
        internal int Index;
        internal int Start;
        internal readonly List<int> Intervals = [];
    }

    private sealed class IntervalOrder : IComparer<CollisionInterval>
    {
        internal static readonly IntervalOrder Instance = new();
        public int Compare(CollisionInterval first, CollisionInterval second)
        {
            int order = first.Minimum.CompareTo(second.Minimum);
            return order != 0 ? order : EntityOrder.Compare(first.Entity, second.Entity);
        }
    }

    private sealed class PartitionOrder : IComparer<IntervalPartition>
    {
        internal static readonly PartitionOrder Instance = new();
        public int Compare(IntervalPartition? first, IntervalPartition? second) => first!.Index.CompareTo(second!.Index);
    }
}
