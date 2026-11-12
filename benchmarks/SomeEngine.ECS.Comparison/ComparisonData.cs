using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

public struct Position : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{
    public float X, Y;
}

public struct Velocity : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{
    public float X, Y;
}

public struct Health : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{
    public int Value, Max;
}

public struct Team : ITag, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public struct Sleeping : ITag, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public struct Extra : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { public int Value; }
public struct GrowthMarker : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { public int Value; }

internal static class ComponentMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WritePosition(ref Position target, long value)
        => Unsafe.WriteUnaligned(ref Unsafe.As<Position, byte>(ref target), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Flip(Span<Health> health)
        => FlipValues(in health);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void FlipValues(in Span<Health> health)
    {
        int count = health.Length;
        ref Health first = ref MemoryMarshal.GetReference(health);
        for (int i = 0; i < count; i++) Unsafe.Add(ref first, i).Max ^= 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
        => MoveValues(in positions, in velocities);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MoveValues(in Span<Position> positionValues, in ReadOnlySpan<Velocity> velocityValues)
    {
        Span<Position> positions = positionValues;
        ReadOnlySpan<Velocity> velocities = velocityValues;
        for (int i = 0; i < positions.Length; i++)
        {
            positions[i].X += velocities[i].X;
            positions[i].Y += velocities[i].Y;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void MoveSimd(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
    {
        Span<float> p = MemoryMarshal.Cast<Position, float>(positions);
        ReadOnlySpan<float> v = MemoryMarshal.Cast<Velocity, float>(velocities);
        int i = 0;
        for (; i + Vector<float>.Count <= p.Length; i += Vector<float>.Count)
            (new Vector<float>(p[i..]) + new Vector<float>(v[i..])).CopyTo(p[i..]);
        for (; i < p.Length; i++) p[i] += v[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReadThree(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
        => ReadThreeValues(in positions, in velocities, in health);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ReadThreeValues(in Span<Position> positionValues, in ReadOnlySpan<Velocity> velocityValues, in ReadOnlySpan<Health> healthValues)
    {
        Span<Position> positions = positionValues;
        ReadOnlySpan<Velocity> velocities = velocityValues;
        ReadOnlySpan<Health> health = healthValues;
        velocities = velocities[..positions.Length];
        health = health[..positions.Length];
        for (int i = 0; i < positions.Length; i++) positions[i].X = velocities[i].X + health[i].Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long QueryMembers(Span<Position> positions, ReadOnlySpan<Velocity> velocities,
        ReadOnlySpan<Health> health, Span<int> members)
    {
        long total = 0, other = 0;
        int i = 0;
        for (; i + 1 < positions.Length; i += 2)
        {
            int first = (int)positions[i].X;
            int second = (int)positions[i + 1].X;
            float value = velocities[i].X + health[i].Value;
            float next = velocities[i + 1].X + health[i + 1].Value;
            positions[i].Y = value;
            positions[i + 1].Y = next;
            members[i] = first;
            members[i + 1] = second;
            total += (int)value;
            other += (int)next;
        }
        for (; i < positions.Length; i++)
        {
            int member = (int)positions[i].X;
            float value = velocities[i].X + health[i].Value;
            positions[i].Y = value;
            members[i] = member;
            total += (int)value;
        }
        return total + other;
    }
}

internal abstract partial class ComparisonWorld(int count, bool signalInInitialLayout = false) : IDisposable
{
    protected readonly bool SignalInInitialLayout = signalInInitialLayout;
    internal const int Changes = 1_000;
    internal const int Lookups = 4_096;
    internal static readonly Position ReplacementPosition = new() { X = 42 };
    internal static readonly long ReplacementBits = Unsafe.BitCast<Position, long>(ReplacementPosition);
    internal int Count { get; } = count;
    internal long ReadTotal;
    internal int EnabledActiveCount, EnabledDisabledCount, EnabledStoredCount;
    internal int ChangeStride, ChangedCount, AddedCount, RemovedCount;
    internal int BufferLength, BufferChangedCount;
    internal long ResourceReleaseCount;
    internal int SharedGroupCount, SharedQueryCount, SharedValueCount, SharedShift;
    internal bool SharedChanges;
    internal int IndexShift, IndexKeyCount, IndexProjectionCalls, IndexFindCount, IndexRangeCount;
    private bool _indexChanges, _indexOrdered;
    private int _indexPrevious;
    private int[] _indexFound = [], _indexRange = [];

    internal void PrepareIndexResult(string workload)
    {
        _indexChanges = workload == "index-change";
        _indexFound = new int[Count];
        _indexRange = new int[Count];
    }

    internal void BeginIndexCycle()
    {
        if (_indexChanges) IndexShift ^= 1;
        IndexProjection.Calls = 0;
        IndexKeyCount = IndexFindCount = IndexRangeCount = 0;
        _indexOrdered = true;
        _indexPrevious = int.MinValue;
    }

    internal void ObserveIndexFind(int ordinal) => _indexFound[IndexFindCount++] = ordinal;
    internal void ObserveIndexRange(int key, int value, int ordinal)
    {
        _indexOrdered &= key >= _indexPrevious && key == value;
        _indexPrevious = key;
        _indexRange[IndexRangeCount++] = ordinal;
    }

    private bool CheckIndexResult()
    {
        bool correct = IndexProjectionCalls == Count / 4 && IndexKeyCount == 16 - 4 * IndexShift
            && IndexFindCount == Count / 16 * (1 + IndexShift) && IndexRangeCount == Count / 2 && _indexOrdered;
        var found = new bool[Count];
        var range = new bool[Count];
        foreach (int ordinal in _indexFound.AsSpan(0, IndexFindCount))
        {
            if ((uint)ordinal >= (uint)Count || found[ordinal]) return false;
            found[ordinal] = true;
        }
        foreach (int ordinal in _indexRange.AsSpan(0, IndexRangeCount))
        {
            if ((uint)ordinal >= (uint)Count || range[ordinal]) return false;
            range[ordinal] = true;
        }
        for (int i = 0; i < Count; i++)
        {
            int key = (i + (i % 4 == 0 ? IndexShift : 0)) % 16;
            correct &= found[i] == (key == 5) && range[i] == (key >= 4 && key <= 11);
        }
        return correct;
    }
    internal int Pick(int index) => (int)((long)index * 7_919 % Count);
    internal abstract void IterateOne();
    internal abstract void IterateTwo();
    internal abstract void IterateSimd();
    internal abstract void IterateThree();
    internal abstract void IterateFiltered();
    internal abstract void ReadRandom();
    internal virtual void ReadRandomCached() => ReadRandom();
    internal virtual void ReadRandomChecked() => ReadRandom();
    internal virtual void ReadRandomCachedChecked() => ReadRandomChecked();
    internal virtual void ReadRandomKnown() => ReadRandom();
    internal abstract void WriteRandom();
    internal virtual void WriteRandomCached() => WriteRandom();
    internal virtual void WriteRandomChecked() => WriteRandom();
    internal virtual void WriteRandomCachedChecked() => WriteRandomChecked();
    internal virtual void WriteRandomKnown() => WriteRandom();
    internal abstract void ReplaceRandom();
    internal virtual void ReplaceBatch() => ReplaceRandom();
    internal abstract void AddRemove();
    internal abstract void CreateDestroyOne();
    internal abstract void CreateDestroyThree();
    internal abstract void SpawnBatch();
    internal abstract void GrowAndReclaim(Action observe);
    internal abstract void EnableableCycle();
    internal abstract bool CheckEnabledValues();
    internal abstract void ChangesCycle();
    internal abstract void BufferCycle();
    internal abstract void ResourceCycle();
    internal abstract void SharedCycle();
    internal abstract void IndexCycle();
    internal abstract void SparseCycle();
    internal abstract void GroupCycle();
    internal abstract Position ReadPosition(int index);
    internal abstract Health ReadHealth(int index);
    internal abstract int EntityCount { get; }
    public abstract void Dispose();

    internal virtual void Prepare(string workload) { }

    internal Action GetAction(string workload)
    {
        SelectGrowthFeature(workload);
        if (workload is "clone-set" or "prefab-set") PrepareCopyForComparison(workload);
        if (workload.StartsWith("commands-")) PrepareCommandsForComparison(workload);
        if (workload.StartsWith("job-")) PrepareQueryJobsComparison(workload);
        Prepare(workload);
        Action action = workload switch
    {
        "iterate-one" => IterateOne,
        "iterate-two" => IterateTwo,
        "iterate-simd" => IterateSimd,
        "iterate-three" => IterateThree,
        "iterate-filtered" => IterateFiltered,
        "read-random" => ReadRandom,
        "read-random-cached" => ReadRandomCached,
        "read-random-checked" => ReadRandomChecked,
        "read-random-cached-checked" => ReadRandomCachedChecked,
        "read-random-known" => ReadRandomKnown,
        "replace-random" => ReplaceRandom,
        "replace-batch" => ReplaceBatch,
        "write-random" => WriteRandom,
        "write-random-cached" => WriteRandomCached,
        "write-random-checked" => WriteRandomChecked,
        "write-random-cached-checked" => WriteRandomCachedChecked,
        "write-random-known" => WriteRandomKnown,
        "add-remove" => AddRemove,
        "create-destroy-one" => CreateDestroyOne,
        "create-destroy-three" => CreateDestroyThree,
        "spawn-batch" => SpawnBatch,
        "enableable" => EnableableCycle,
        "changes-1" or "changes-25" => ChangesCycle,
        "buffer-4" or "buffer-32" => BufferCycle,
        "resources" => ResourceCycle,
        "shared-same-16" or "shared-change-16" or "shared-same-unique" or "shared-change-unique" => SharedCycle,
        "index-same" or "index-change" => IndexCycle,
        "sparse-1" or "sparse-25" => SparseCycle,
        "group-same" or "group-change" => GroupCycle,
        "query-append" or "query-append-fixed" => QueryAppendCycle,
        "query-bind" or "query-bind-fixed" => QueryBindCycle,
        _ when workload.StartsWith("hierarchy-") => HierarchyCycle,
        "commands-1" or "commands-4" => CommandCycle,
        "clone-set" or "prefab-set" => CopyCycle,
        _ when workload.StartsWith("job-") => QueryJobsCycle,
        _ when workload.StartsWith("relation-") => RelationCycle,
        _ => throw new ArgumentException(workload),
        };
        return BufferActionThread.UsesActionThread(workload) ? BufferActionThread.Wrap(action) : action;
    }

    internal void CheckResult(string workload, int actionRuns = 1)
    {
        bool correct = EntityCount == ExpectedEntityCount && CheckGrowthResult();
        switch (workload)
        {
            case var job when job.StartsWith("job-"): correct &= CheckQueryJobs(actionRuns); break;
            case "clone-set":
            case "prefab-set": correct &= CopyScenario!.Check(); break;
            case "commands-1":
            case "commands-4": correct &= CommandScenario!.Check(); break;
            case var relation when relation.StartsWith("relation-"): correct &= RelationScenario!.Check(); break;
            case var hierarchy when hierarchy.StartsWith("hierarchy-"): correct &= HierarchyScenario!.Check(); break;
            case "query-append":
            case "query-bind":
            case "query-bind-fixed":
            case "query-append-fixed": correct &= CheckQueryAppendResult(); break;
            case "index-same":
            case "index-change": correct &= CheckIndexResult(); break;
            case "sparse-1":
            case "sparse-25": correct &= CheckSparseResult(); break;
            case "group-same":
            case "group-change": correct &= CheckGroupResult(); break;
            case "resources": correct &= ReadTotal == 171_376 && ResourceReleaseCount == Lookups + 16; break;
            case "shared-same-16":
            case "shared-change-16":
            case "shared-same-unique":
            case "shared-change-unique":
                long sharedTotal = 0;
                for (int i = 0; i < Lookups; i++)
                {
                    int entity = Pick(i);
                    sharedTotal += (entity + (entity % 4 == 0 ? SharedShift : 0)) % SharedGroupCount;
                }
                correct &= ReadTotal == sharedTotal && SharedValueCount == SharedGroupCount * (GrowthPerformed ? 2 : 1)
                    && SharedQueryCount == Count / SharedGroupCount * 8;
                break;
            case "enableable":
                correct &= EnabledActiveCount == Count * 3 / 4 && EnabledDisabledCount == Count / 4
                    && EnabledStoredCount == Count / 4 && ReadTotal == Count / 4 * 42L && CheckEnabledValues();
                break;
            case "changes-1":
            case "changes-25":
                int changes = Count / ChangeStride;
                correct &= ChangedCount == changes && AddedCount == changes && RemovedCount == changes
                    && ReadTotal == changes * 99L;
                break;
            case "buffer-4":
            case "buffer-32":
                correct &= ReadTotal == Count * (2L * BufferLength + 1) && BufferChangedCount == Count
                    && CheckBufferEntityValues();
                break;
            case "iterate-one": correct &= CheckHealthValues(false); break;
            case "iterate-filtered": correct &= CheckHealthValues(true); break;
            case "iterate-two":
            case "iterate-simd": correct &= CheckPositionValues(false, actionRuns); break;
            case "iterate-three": correct &= CheckPositionValues(true); break;
            case "replace-random":
            case "replace-batch":
            case "write-random":
            case "write-random-checked":
            case "write-random-cached-checked":
            case "write-random-known":
            case "write-random-cached": correct &= ReadPosition(0).X == 42; break;
            case "read-random":
            case "read-random-checked":
            case "read-random-cached-checked":
            case "read-random-known":
            case "read-random-cached":
                long expected = 0;
                for (int i = 0; i < Lookups; i++) expected += Pick(i);
                correct &= ReadTotal == expected;
                break;
        }
        if (!correct) throw new InvalidOperationException($"{GetType().Name}: {workload} result check failed; changed={ChangedCount}, added={AddedCount}, removed={RemovedCount}, total={ReadTotal}.");
    }

    private bool CheckBufferEntityValues()
    {
        for (int i = 0; i < Count; i++)
        {
            Position position = ReadPosition(i);
            Health health = ReadHealth(i);
            if (position.X != i || position.Y != 0 || health.Value != 100 || health.Max != 100) return false;
        }
        return true;
    }

    private bool CheckHealthValues(bool filtered)
    {
        for (int i = 0; i < Count; i++)
        {
            Health value = ReadHealth(i);
            int expected = !filtered || i % 4 == 2 ? 101 : 100;
            if (value.Value != 100 || value.Max != expected) return false;
        }
        return true;
    }

    private bool CheckPositionValues(bool readThree, int actionRuns = 1)
    {
        for (int i = 0; i < Count; i++)
        {
            Position value = ReadPosition(i);
            if (value.X != (readThree ? 101 : i + actionRuns) || value.Y != (readThree ? 0 : 2 * actionRuns)) return false;
        }
        return true;
    }
}
