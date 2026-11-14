using SomeEngine.ECS.Systems;
using EasyEcs;
using Friflo.Engine.ECS;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Comparison;

// Fixed mode caps each public range at 256 rows. SomeEngine uses fixed 256-row storage chunks;
// the references expose bounded views over their native chunks or columns.
internal struct QueryAppendPass(int[] members, int rangeLimit) : EasyEcs.IForEachBatch<Position, Velocity, Health>
{
    internal int Count, Ranges, MaximumRange;
    internal long Total;
    public static int Writes => 1;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
        => ExecuteValues(in positions, in velocities, in health);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void ExecuteValues(in Span<Position> positionValues, in ReadOnlySpan<Velocity> velocityValues, in ReadOnlySpan<Health> healthValues)
    {
        Span<Position> positions = positionValues;
        ReadOnlySpan<Velocity> velocities = velocityValues;
        ReadOnlySpan<Health> health = healthValues;
        int count = Count, ranges = Ranges, maximumRange = MaximumRange;
        long total = Total;
        int[] output = members;
        int limit = rangeLimit == 0 ? positions.Length : rangeLimit;
        for (int offset = 0; offset < positions.Length; offset += limit)
        {
            int length = Math.Min(limit, positions.Length - offset);
            ranges++;
            maximumRange = Math.Max(maximumRange, length);
            total += ComponentMath.QueryMembers(positions.Slice(offset, length), velocities.Slice(offset, length),
                health.Slice(offset, length), output.AsSpan(count, length));
            count += length;
        }
        Count = count; Ranges = ranges; MaximumRange = maximumRange; Total = total;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute(EasyEcs.EntitySpan entities, Span<Position> positions, Span<Velocity> velocities, Span<Health> health)
        => Execute(positions, velocities, health);
}

internal abstract partial class ComparisonWorld
{
    internal bool QueryAppendReady;
    internal bool QueryBindingOnly;
    private int[] _queryMembers = [], _queryRestored = [];
    private int _queryLimit, _queryInitialCount;
    private int _queryPublishedExpanded, _queryPublishedRestored;
    private QueryAppendPass _queryExpandedPass, _queryRestoredPass;
    internal void PrepareQueryAppendResult(string workload, int initialCount)
    {
        _queryInitialCount = initialCount;
        QueryBindingOnly = workload.StartsWith("query-bind");
        _queryLimit = workload.EndsWith("fixed") ? 256 : 0;
        _queryMembers = QueryBindingOnly ? [] : new int[Count * 2 + 1];
        _queryRestored = new int[Count];
        QueryAppendReady = true;
    }
    internal QueryAppendPass NewQueryAppendPass(bool restored)
    {
        if (!restored && _queryMembers.Length == 0) _queryMembers = new int[Count * 2 + 1];
        return new(restored ? _queryRestored : _queryMembers, _queryLimit);
    }
    internal void FinishQueryAppend(in QueryAppendPass expanded, in QueryAppendPass restored, int expandedCount, int restoredCount)
        => (_queryExpandedPass, _queryRestoredPass, _queryPublishedExpanded, _queryPublishedRestored)
            = (expanded, restored, expandedCount, restoredCount);
    internal void FinishQueryBind(in QueryAppendPass pass, int count)
        => (_queryRestoredPass, _queryPublishedRestored) = (pass, count);
    internal bool CheckQueryAppendResult()
    {
        if (QueryBindingOnly && _queryExpandedPass.Count == 0)
            return _queryInitialCount == Count && _queryRestoredPass.Count == Count && _queryPublishedRestored == Count
                && _queryRestoredPass.Total == Count * 101L
                && (_queryLimit == 0 || _queryRestoredPass.MaximumRange <= _queryLimit) && CheckMembers(_queryRestored, Count);
        if (_queryInitialCount != Count || _queryExpandedPass.Count != Count * 2 + 1 || _queryRestoredPass.Count != Count
            || _queryPublishedExpanded != _queryExpandedPass.Count || _queryPublishedRestored != _queryRestoredPass.Count
            || _queryExpandedPass.Total != (Count * 2L + 1) * 101 || _queryRestoredPass.Total != Count * 101L) return false;
        if (_queryLimit != 0 && (_queryExpandedPass.MaximumRange > _queryLimit || _queryRestoredPass.MaximumRange > _queryLimit)) return false;
        return CheckMembers(_queryMembers, Count * 2 + 1) && CheckMembers(_queryRestored, Count);
    }
    private static bool CheckMembers(int[] members, int count)
    {
        var seen = new bool[count];
        foreach (int ordinal in members)
        {
            if ((uint)ordinal >= (uint)count || seen[ordinal]) return false;
            seen[ordinal] = true;
        }
        return true;
    }
    internal abstract void QueryAppendCycle();
    internal abstract void QueryBindCycle();
}

internal sealed partial class SomeEngineWorld
{
    private Query _queryAppend;
    private Body[] _queryAppendBodies = [];
    private readonly JobResult<QueryAppendPass> _queryAppendPass = new();

    private readonly partial struct QueryAppendJob(JobResult<QueryAppendPass> pass) : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
            => pass.Value.Execute(positions, velocities, health);
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(pass);
    }
    private SomeEngine.ECS.Entities.Entity[] _queryAdded = [];
    private void PrepareQueryAppend(string workload)
    {
        if (QueryAppendReady) return;
        _queryAppend = _world.Query<Position, Velocity, Health>();
        PrepareQueryAppendResult(workload, _queryAppend.Count);
        if (!QueryBindingOnly) _queryAdded = new SomeEngine.ECS.Entities.Entity[Count + 1];
    }
    internal override void QueryAppendCycle()
    {
        if (_queryAdded.Length == 0) _queryAdded = new SomeEngine.ECS.Entities.Entity[Count + 1];
        _world.ReserveBundle<Body>(Count * 3 / 2);
        if (_queryAppendBodies.Length != Count)
        {
            ReadOnlySpan<Position> appended = PositionInputs.Append(Count);
            _queryAppendBodies = new Body[Count];
            for (int row = 0; row < Count; row++) _queryAppendBodies[row] = Body.Moving(appended[row]);
        }
        _world.Spawn<Body>(_queryAppendBodies, _queryAdded.AsSpan(0, Count));
        _queryAdded[Count] = _world.Spawn(new GrowthBody { Body = Body.Moving(new Position { X = Count * 2 }) });
        var expanded = NewQueryAppendPass(false);
        int expandedCount = ExecuteQueryPass(ref expanded);
        _world.Destroy(_queryAdded);
        var restored = NewQueryAppendPass(true);
        int restoredCount = ExecuteQueryPass(ref restored);
        FinishQueryAppend(in expanded, in restored, expandedCount, restoredCount);
    }
    internal override void QueryBindCycle()
    {
        var pass = NewQueryAppendPass(true);
        int count = ExecuteQueryPass(ref pass);
        FinishQueryBind(in pass, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ExecuteQueryPass(ref QueryAppendPass pass)
    {
        if (_queryAppend.TryGetValues<Position, Velocity, Health>(out var positions, out var velocities, out var health))
        {
            pass.ExecuteValues(in positions, in velocities, in health);
            return positions.Length;
        }
        int count = _queryAppend.Count;
        QueryBindRanges(ref pass);
        return count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void QueryBindRanges(ref QueryAppendPass pass)
    {
        _queryAppendPass.Value = pass;
        new QueryAppendJob(_queryAppendPass).Execute(_world, query: _queryAppend);
        pass = _queryAppendPass.Value;
    }

}

internal sealed partial class EasyEcsWorld
{

    private EasyEcs.Entity[] _queryAdded = [];
    private void PrepareQueryAppend(string workload)
    {
        if (QueryAppendReady) return;

        PrepareQueryAppendResult(workload, _world.Count<Position, Velocity, Health>());
        if (!QueryBindingOnly) _queryAdded = new EasyEcs.Entity[Count + 1];
    }
    internal override void QueryAppendCycle()
    {
        if (_queryAdded.Length == 0) _queryAdded = new EasyEcs.Entity[Count + 1];
        _world.EnsureCapacity<Position, Velocity, Health>(Count * 3 / 2);
        Span<EasyEcs.Entity> added = _queryAdded.AsSpan(0, Count);
        _world.CreateEntities(added, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        PositionInputs.Append(Count).AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref added[0].Get<Position>(), Count));
        _queryAdded[Count] = _world.CreateEntity(new Position { X = Count * 2 }, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new GrowthMarker());
        int expandedCount = _world.Count<Position, Velocity, Health>();
        var expanded = NewQueryAppendPass(false);
        _world.ForEachBatch<Position, Velocity, Health, QueryAppendPass>(ref expanded);
        foreach (var entity in _queryAdded) entity.Destroy();
        var restored = NewQueryAppendPass(true);
        _world.ForEachBatch<Position, Velocity, Health, QueryAppendPass>(ref restored);
        FinishQueryAppend(in expanded, in restored, expandedCount, _world.Count<Position, Velocity, Health>());
    }
    internal override void QueryBindCycle()
    {
        int count = _world.Count<Position, Velocity, Health>();
        var pass = NewQueryAppendPass(true);
        _world.ForEachBatch<Position, Velocity, Health, QueryAppendPass>(ref pass);
        FinishQueryBind(in pass, count);
    }
}

internal sealed partial class FrifloWorld
{
    private ArchetypeQuery<Position, Velocity, Health>? _queryAppend;
    private Friflo.Engine.ECS.Entity[] _queryAdded = [];
    private void PrepareQueryAppend(string workload)
    {
        if (QueryAppendReady) return;
        _queryAppend = _world.Query<Position, Velocity, Health>();
        PrepareQueryAppendResult(workload, _queryAppend.Count);
        if (!QueryBindingOnly) _queryAdded = new Friflo.Engine.ECS.Entity[Count + 1];
    }
    internal override void QueryAppendCycle()
    {
        if (_queryAdded.Length == 0) _queryAdded = new Friflo.Engine.ECS.Entity[Count + 1];
        _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health>(), default).EnsureCapacity(Count * 3 / 2);
        Position[] input = PositionInputs.Append(Count);
        var batch = _world.Batch(autoReturn: false).Add(new Position()).Add(new Velocity { X = 1, Y = 2 })
            .Add(new Health { Value = 100, Max = 100 });
        ref Position position = ref batch.Get<Position>();
        for (int i = 0; i < Count; i++)
        {
            position = input[i];
            _queryAdded[i] = batch.CreateEntity();
        }
        batch.Return();
        _queryAdded[Count] = _world.CreateEntity(new Position { X = Count * 2 }, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new GrowthMarker());
        int expandedCount = _queryAppend!.Count;
        var expanded = NewQueryAppendPass(false);
        foreach (var (positions, velocities, health, _) in _queryAppend!.Chunks)
            expanded.Execute(positions.Span, velocities.Span, health.Span);
        foreach (var entity in _queryAdded) entity.DeleteEntity();
        var restored = NewQueryAppendPass(true);
        foreach (var (positions, velocities, health, _) in _queryAppend!.Chunks)
            restored.Execute(positions.Span, velocities.Span, health.Span);
        FinishQueryAppend(in expanded, in restored, expandedCount, _queryAppend!.Count);
    }
    internal override void QueryBindCycle()
    {
        int count = _queryAppend!.Count;
        var pass = NewQueryAppendPass(true);
        foreach (var (positions, velocities, health, _) in _queryAppend!.Chunks)
            pass.Execute(positions.Span, velocities.Span, health.Span);
        FinishQueryBind(in pass, count);
    }
}

internal sealed partial class ArchWorld
{
    private Arch.Core.QueryDescription _queryAppend;
    private Arch.Core.Entity[] _queryAdded = [];
    private void PrepareQueryAppend(string workload)
    {
        if (QueryAppendReady) return;
        _queryAppend = new Arch.Core.QueryDescription().WithAll<Position, Velocity, Health>();
        PrepareQueryAppendResult(workload, _world.CountEntities(in _queryAppend));
        if (!QueryBindingOnly) _queryAdded = new Arch.Core.Entity[Count + 1];
    }
    internal override void QueryAppendCycle()
    {
        if (_queryAdded.Length == 0) _queryAdded = new Arch.Core.Entity[Count + 1];
        _world.EnsureCapacity(ThreeComponents, Count * 3 / 2);
        _world.Create(_queryAdded.AsSpan(0, Count), in ThreeComponents, Count);
        Position[] input = PositionInputs.Append(Count);
        for (int i = 0; i < Count; i++)
            _world.Get<Position>(_queryAdded[i]) = input[i];
        foreach (ref var chunk in _world.Query(in _queryAppend).GetChunkIterator())
        {
            chunk.GetSpan<Velocity>()[..chunk.Count].Fill(new Velocity { X = 1, Y = 2 });
            chunk.GetSpan<Health>()[..chunk.Count].Fill(new Health { Value = 100, Max = 100 });
        }
        _queryAdded[Count] = _world.Create(new Position { X = Count * 2 }, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new GrowthMarker());
        int expandedCount = _world.CountEntities(in _queryAppend);
        var expanded = NewQueryAppendPass(false);
        foreach (ref var chunk in _world.Query(in _queryAppend).GetChunkIterator())
            expanded.Execute(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count], chunk.GetSpan<Health>()[..chunk.Count]);
        foreach (var entity in _queryAdded) _world.Destroy(entity);
        var restored = NewQueryAppendPass(true);
        foreach (ref var chunk in _world.Query(in _queryAppend).GetChunkIterator())
            restored.Execute(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count], chunk.GetSpan<Health>()[..chunk.Count]);
        FinishQueryAppend(in expanded, in restored, expandedCount, _world.CountEntities(in _queryAppend));
    }
    internal override void QueryBindCycle()
    {
        int count = _world.CountEntities(in _queryAppend);
        var pass = NewQueryAppendPass(true);
        foreach (ref var chunk in _world.Query(in _queryAppend).GetChunkIterator())
            pass.Execute(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count], chunk.GetSpan<Health>()[..chunk.Count]);
        FinishQueryBind(in pass, count);
    }
}

internal sealed partial class DefaultEcsWorld
{
    private DefaultEcs.EntitySet? _queryAppend;
    private DefaultEcs.Entity[] _queryAdded = [];
    private void PrepareQueryAppend(string workload)
    {
        if (QueryAppendReady) return;
        _queryAppend = _world.GetEntities().With<Position>().With<Velocity>().With<Health>().AsSet();
        PrepareQueryAppendResult(workload, _queryAppend.Count);
        if (!QueryBindingOnly) _queryAdded = new DefaultEcs.Entity[Count + 1];
    }
    internal override void QueryAppendCycle()
    {
        if (_queryAdded.Length == 0) _queryAdded = new DefaultEcs.Entity[Count + 1];
        Position[] input = PositionInputs.Append(Count);
        for (int i = 0; i < Count; i++)
            _queryAdded[i] = CreateQueryRow(input[i], false);
        _queryAdded[Count] = CreateQueryRow(new Position { X = Count * 2 }, true);
        int expandedCount = _queryAppend!.Count;
        var expanded = NewQueryAppendPass(false);
        expanded.Execute(_world.GetAll<Position>(), _world.GetAll<Velocity>(), _world.GetAll<Health>());
        foreach (var entity in _queryAdded) entity.Dispose();
        var restored = NewQueryAppendPass(true);
        restored.Execute(_world.GetAll<Position>(), _world.GetAll<Velocity>(), _world.GetAll<Health>());
        FinishQueryAppend(in expanded, in restored, expandedCount, _queryAppend!.Count);
    }
    internal override void QueryBindCycle()
    {
        var pass = NewQueryAppendPass(true);
        Span<Position> positions = _world.GetAll<Position>();
        int count = positions.Length;
        pass.Execute(positions, _world.GetAll<Velocity>(), _world.GetAll<Health>());
        FinishQueryBind(in pass, count);
    }
    private DefaultEcs.Entity CreateQueryRow(in Position position, bool marker)
    {
        var entity = _world.CreateEntity();
        entity.Set(position);
        entity.Set(new Velocity { X = 1, Y = 2 });
        entity.Set(new Health { Value = 100, Max = 100 });
        if (marker) entity.Set(new GrowthMarker());
        return entity;
    }
}
