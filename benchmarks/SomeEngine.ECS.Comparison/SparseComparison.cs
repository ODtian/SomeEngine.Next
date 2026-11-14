namespace SomeEngine.ECS.Comparison;

internal abstract partial class ComparisonWorld
{
    internal int SparseStride, SparseDenseCount, SparseMixedCount, SparseEmptyCount, SparseFinalCount;
    internal long SparseDenseTotal, SparseReleased;
    internal readonly ResourceCounter SparseCounter = new();
    internal int[] SparseLookupIndices = [], SparseMembers = [];

    internal void PrepareSparseResult(string workload)
    {
        SparseStride = workload == "sparse-1" ? 100 : 4;
        int attached = Count / SparseStride;
        SparseLookupIndices = new int[Lookups];
        SparseMembers = new int[attached];
        for (int i = 0; i < Lookups; i++) SparseLookupIndices[i] = (int)((long)i * 7_919 % attached) * SparseStride;
    }

    internal void BeginSparseCycle()
    {
        SparseCounter.Count = 0;
        ReadTotal = SparseDenseTotal = 0;
        SparseDenseCount = 0;
    }

    internal void RunSparseCycle<TEntity, TAccess>(ReferenceSparseStores<TEntity, TAccess> stores, TEntity[] entities)
        where TEntity : struct where TAccess : struct, IReferenceSparseAccess<TEntity>
    {
        BeginSparseCycle();
        foreach (int ordinal in SparseLookupIndices)
        {
            TEntity entity = entities[ordinal];
            ReadTotal += stores.Read<SparseNumber>(entity).Value;
            stores.Set(entity, new SparseNumber(SparseCounter, 42, ordinal));
        }
        using (var values = stores.OpenRead<SparseNumber>())
            foreach (ref readonly var value in values.Values)
            {
                SparseDenseTotal += value.Value;
                if (ReferenceEquals(value.Counter, SparseCounter)) SparseDenseCount++;
            }
        SparseMixedCount = stores.Mixed<SparseNumber, SparseWeight>(SparseMembers);
        for (int i = 0; i < Count; i += SparseStride * 2) stores.Remove<SparseNumber>(entities[i]);
        for (int i = SparseStride; i < Count; i += SparseStride * 2) stores.Destroy(entities[i]);
        SparseEmptyCount = stores.Mixed<SparseNumber, SparseWeight>(SparseMembers);
        SparseReleased = SparseCounter.Count;
        for (int i = 0; i < Count; i += SparseStride * 2) stores.Remove<SparseWeight>(entities[i]);
        for (int i = SparseStride; i < Count; i += SparseStride * 2) entities[i] = stores.Access.Create(i);
        for (int i = 0; i < Count; i += SparseStride)
        {
            stores.Set(entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) stores.Set(entities[i], new SparseWeight(i));
        }
        using var complete = stores.OpenRead<SparseNumber>();
        SparseFinalCount = complete.Values.Length;
    }

    internal bool CheckSparseResult()
    {
        int attached = Count / SparseStride;
        int uniqueReads = Math.Min(attached, Lookups);
        bool correct = ReadTotal == Lookups * 42L - uniqueReads * 41L
            && SparseDenseTotal == attached + uniqueReads * 41L && SparseDenseCount == attached
            && SparseMixedCount == attached / 2 && SparseEmptyCount == 0 && SparseFinalCount == attached
            && SparseReleased == Lookups + attached;
        var seen = new bool[Count];
        foreach (int ordinal in SparseMembers.AsSpan(0, SparseMixedCount))
        {
            if ((uint)ordinal >= (uint)Count || seen[ordinal]) return false;
            seen[ordinal] = true;
        }
        for (int i = 0; i < Count; i++) correct &= seen[i] == (i % (SparseStride * 2) == 0);
        return correct;
    }
}

internal sealed partial class SomeEngineWorld
{
    private SparseQuery? _mixedSparse;
    private void PrepareSparse(string workload)
    {
        if (_mixedSparse is not null) return;
        PrepareSparseResult(workload);
        for (int i = 0; i < Count; i += SparseStride)
        {
            _world.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _world.Set(_entities[i], new SparseWeight(i));
        }
        _mixedSparse = _world.QuerySparse<SparseNumber>(new QueryDescription().With<Position>().With<Team>()).With<SparseWeight>();
    }
    private int ReadSparseMembers()
    {
        using var members = _mixedSparse!.Open();
        int count = 0;
        foreach (var entity in members) SparseMembers[count++] = _world.Read<SparseNumber>(entity).Ordinal;
        return count;
    }
    internal override void SparseCycle()
    {
        BeginSparseCycle();
        foreach (int ordinal in SparseLookupIndices)
        {
            var entity = _entities[ordinal];
            ReadTotal += _world.Read<SparseNumber>(entity).Value;
            _world.Set(entity, new SparseNumber(SparseCounter, 42, ordinal));
        }
        using (var values = _world.OpenSparseValues<SparseNumber>())
            foreach (ref readonly var value in values.Values)
            {
                SparseDenseTotal += value.Value;
                if (ReferenceEquals(value.Counter, SparseCounter)) SparseDenseCount++;
            }
        SparseMixedCount = ReadSparseMembers();
        for (int i = 0; i < Count; i += SparseStride * 2) _world.Remove<SparseNumber>(_entities[i]);
        for (int i = SparseStride; i < Count; i += SparseStride * 2) _world.Destroy(_entities[i]);
        SparseEmptyCount = ReadSparseMembers();
        SparseReleased = SparseCounter.Count;
        for (int i = 0; i < Count; i += SparseStride * 2) _world.Remove<SparseWeight>(_entities[i]);
        for (int i = SparseStride; i < Count; i += SparseStride * 2)
            _entities[i] = _world.Spawn(new SleepingTeamBody { Body = Body.Moving(new Position { X = i }) });
        for (int i = 0; i < Count; i += SparseStride)
        {
            _world.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _world.Set(_entities[i], new SparseWeight(i));
        }
        using var complete = _world.OpenSparseValues<SparseNumber>();
        SparseFinalCount = complete.Count;
    }
}

internal sealed partial class EasyEcsWorld
{
    private ReferenceSparseStores<EasyEcs.Entity, EasySparseAccess>? _sparseValues;
    private void PrepareSparse(string workload)
    {
        if (_sparseValues is not null) return;
        PrepareSparseResult(workload);
        _sparseValues = new(new EasySparseAccess(_world), Count + Changes);
        for (int i = 0; i < Count; i += SparseStride)
        {
            _sparseValues.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _sparseValues.Set(_entities[i], new SparseWeight(i));
        }
    }
    internal override void SparseCycle() => RunSparseCycle(_sparseValues!, _entities);
}

internal sealed partial class FrifloWorld
{
    private ReferenceSparseStores<Friflo.Engine.ECS.Entity, FrifloSparseAccess>? _sparseValues;
    private void PrepareSparse(string workload)
    {
        if (_sparseValues is not null) return;
        PrepareSparseResult(workload);
        _sparseValues = new(new FrifloSparseAccess(_world), Count + Changes);
        for (int i = 0; i < Count; i += SparseStride)
        {
            _sparseValues.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _sparseValues.Set(_entities[i], new SparseWeight(i));
        }
    }
    internal override void SparseCycle() => RunSparseCycle(_sparseValues!, _entities);
}

internal sealed partial class ArchWorld
{
    private ReferenceSparseStores<Arch.Core.Entity, ArchSparseAccess>? _sparseValues;
    private void PrepareSparse(string workload)
    {
        if (_sparseValues is not null) return;
        PrepareSparseResult(workload);
        _sparseValues = new(new ArchSparseAccess(_world), Count + Changes);
        for (int i = 0; i < Count; i += SparseStride)
        {
            _sparseValues.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _sparseValues.Set(_entities[i], new SparseWeight(i));
        }
    }
    internal override void SparseCycle() => RunSparseCycle(_sparseValues!, _entities);
}

internal sealed partial class DefaultEcsWorld
{
    private ReferenceSparseStores<DefaultEcs.Entity, DefaultSparseAccess>? _sparseValues;
    private void PrepareSparse(string workload)
    {
        if (_sparseValues is not null) return;
        PrepareSparseResult(workload);
        _sparseValues = new(new DefaultSparseAccess(_world, new DefaultSparseIds()), Count + Changes);
        for (int i = 0; i < Count; i += SparseStride)
        {
            _sparseValues.Set(_entities[i], new SparseNumber(SparseCounter, 1, i));
            if (i % (SparseStride * 2) == 0) _sparseValues.Set(_entities[i], new SparseWeight(i));
        }
    }
    internal override void SparseCycle() => RunSparseCycle(_sparseValues!, _entities);
}
