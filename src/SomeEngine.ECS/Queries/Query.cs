using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.ECS;

public sealed class QueryDescription
{
    private QueryDefinition? _definition;
    internal QueryDefinition Definition => _definition ??= QueryDefinitions.Get(this);
    internal readonly record struct SharedCondition(int Type, int Value);
    internal readonly record struct GroupCondition(int Type, int Value);
    internal ComponentBits Required;
    internal ComponentBits Stored;
    internal ComponentBits Any;
    internal ComponentBits OptionalReads;
    internal ComponentBits Excluded;
    internal ComponentBits Disabled;
    internal ComponentBits Changed;
    internal ComponentBits Added;
    // Sparse types are not archetype columns. Their conditions are checked per row against the sparse stores, and
    // their reads are declared with sparse storage.
    internal ComponentBits SparseRequired;
    internal ComponentBits SparseExcluded;
    internal ComponentBits SparseReads;
    internal bool HasSparseConditions => SparseRequired.Count + SparseExcluded.Count != 0;
    internal ulong Since;
    internal SharedCondition[] Shared = [];
    internal GroupCondition[] Groups = [];

    public QueryDescription With<T>() where T : struct
    {
        _definition = null;
        int type = ComponentType<T>.TypeId;
        if (ComponentType<T>.IsSparseType) { SparseRequired.Add(type); return this; }
        Required.Add(type);
        Stored.Remove(type);
        Disabled.Remove(type);
        return this;
    }
    public QueryDescription WithStored<T>() where T : struct
    {
        _definition = null;
        int type = ComponentType<T>.TypeId;
        if (ComponentType<T>.IsSparseType) { SparseRequired.Add(type); return this; }
        Required.Add(type);
        Stored.Add(type);
        Disabled.Remove(type);
        return this;
    }
    public QueryDescription WithAny<T>() where T : struct
    {
        if (ComponentType<T>.IsSparseType) throw new InvalidOperationException("A sparse type joins a query through With, Without, WithChanged, WithAdded or Optional.");
        _definition = null; Any.Add(ComponentType<T>.TypeId); return this;
    }
    public QueryDescription Optional<T>() where T : struct
    {
        _definition = null;
        if (ComponentType<T>.IsSparseType) SparseReads.Add(ComponentType<T>.TypeId);
        else OptionalReads.Add(ComponentType<T>.TypeId);
        return this;
    }
    public QueryDescription WithAccess(QueryDescription inputs)
    {
        _definition = null;
        foreach (int type in inputs.Definition.AccessTypes) OptionalReads.Add(type);
        return this;
    }
    public QueryDescription InGroup<T>(int value) where T : struct, IGroupComponent
    {
        _definition = null;
        int type = ComponentType<T>.TypeId;
        Required.Add(type);
        for (int i = 0; i < Groups.Length; i++)
            if (Groups[i].Type == type) { Groups[i] = new(type, value); return this; }
        int count = Groups.Length;
        Array.Resize(ref Groups, count + 1);
        Groups[count] = new(type, value);
        return this;
    }

    internal bool MatchesGroups(Archetype archetype)
    {
        foreach (GroupCondition condition in Groups)
            if (!archetype.TryGroup(condition.Type, out int value) || value != condition.Value) return false;
        return true;
    }
    public QueryDescription WithShared<T>(SharedId<T> id) where T : struct, ISharedComponent
    {
        _definition = null;
        int type = ComponentType<T>.TypeId;
        Required.Add(type);
        for (int i = 0; i < Shared.Length; i++)
            if (Shared[i].Type == type) { Shared[i] = new(type, id.Value); return this; }
        int count = Shared.Length;
        Array.Resize(ref Shared, count + 1);
        Shared[count] = new(type, id.Value);
        return this;
    }

    public QueryDescription Without<T>() where T : struct
    {
        _definition = null;
        if (ComponentType<T>.IsSparseType) SparseExcluded.Add(ComponentType<T>.TypeId);
        else Excluded.Add(ComponentType<T>.TypeId);
        return this;
    }
    public QueryDescription WithDisabled<T>() where T : struct, IEnableableComponent
    {
        if (ComponentType<T>.IsSparseType) throw new InvalidOperationException("A sparse type joins a query through With, Without, WithChanged, WithAdded or Optional.");
        _definition = null;
        int type = ComponentType<T>.TypeId;
        Required.Add(type);
        Stored.Remove(type);
        Disabled.Add(type);
        return this;
    }

    public QueryDescription WithChanged<T>(ulong since) where T : struct, ITrackedComponent
    {
        _definition = null;
        int type = ComponentType<T>.TypeId;
        if (ComponentType<T>.IsSparseType) SparseRequired.Add(type);
        else Required.Add(type);
        Changed.Add(type);
        Since = since;
        return this;
    }

    public QueryDescription WithAdded<T>(ulong since) where T : struct, IAddedComponent
    {
        WithChanged<T>(since);
        Added.Add(ComponentType<T>.TypeId);
        return this;
    }

    public QueryDescription WithChangedBuffer<T>(ulong since) where T : struct, ITrackedComponent
    {
        _definition = null;
        int type = ComponentType<Buffer<T>>.TypeId;
        Required.Add(type);
        Changed.Add(type);
        Since = since;
        return this;
    }

    public QueryDescription WithAddedBuffer<T>(ulong since) where T : struct, IAddedComponent
    {
        WithChangedBuffer<T>(since);
        Added.Add(ComponentType<Buffer<T>>.TypeId);
        return this;
    }

    internal QueryDescription Copy() => new()
    {
        Required = Required.Copy(), Stored = Stored.Copy(), Excluded = Excluded.Copy(), Disabled = Disabled.Copy(),
        Any = Any.Copy(), OptionalReads = OptionalReads.Copy(),
        Changed = Changed.Copy(), Added = Added.Copy(), Since = Since,
        SparseRequired = SparseRequired.Copy(), SparseExcluded = SparseExcluded.Copy(), SparseReads = SparseReads.Copy(),
        Shared = Shared.ToArray(),
        Groups = Groups.ToArray(),
    };
}

public partial struct Query
{
    private readonly World _world;
    private readonly QueryData _data;
    private PodColumnBuffer? _singleValues;
    private ulong _since;
    private readonly QueryDescription _description => _data.Description;
    private readonly QueryMatch[] _matches => _data.Matches;
    private readonly int _count => _data.Count;
    private readonly bool _countShared => _data.CountShared;
    internal readonly ulong SinceVersion => _since;
    internal readonly World World => _world;
    internal readonly QueryDescription Description => _data.Description;
    internal readonly QueryData Data => _data;
    public readonly ulong MembershipVersion => _world.ReadMembershipVersion(_data);

    internal Query(World world, QueryData data, ulong since)
        => (_world, _data, _singleValues, _since)
            = (world, data, data.SingleValues, since);

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_world.Execution is null)
            {
                RefreshIfNeeded();
                if (_data.AllRows) return _world.EntityCount;
            }
            return CountMatches();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int CountMatches()
    {
            using WorldAccess access = _world.OpenQuery(this, []);
            if (_countShared)
            {
                var shared = _description.Shared[0];
                return _world.SharedCount(shared.Type, shared.Value);
            }
            _world.BeginRead();
            try
            {
                RefreshIfNeeded();
                if (_data.AllRows) return _world.EntityCount;
                int count = 0;
                if (!_world.HasMultipleChunks)
                {
                    for (int i = 0; i < _count; i++)
                    {
                        QueryMatch match = _matches[i];
                        Chunk chunk = match.Archetype.PrimaryChunk;
                        count += match.Filter is null ? chunk.Count : match.Filter.Count(chunk, _since);
                    }
                }
                else
                    for (int i = 0; i < _count; i++)
                    {
                        QueryMatch match = _matches[i];
                        foreach (Chunk chunk in match.Archetype.AllChunks)
                            count += match.Filter is null ? chunk.Count : match.Filter.Count(chunk, _since);
                    }
                return count;
            }
            finally { _world.EndRead(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Any()
    {
        if (_world.Execution is null)
        {
            RefreshIfNeeded();
            if (_data.AllRows) return _world.EntityCount != 0;
        }
        return AnyMatch();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool AnyMatch()
    {
        using WorldAccess access = _world.OpenQuery(this, []);
        if (_countShared)
        {
            var shared = _description.Shared[0];
            return _world.SharedCount(shared.Type, shared.Value) != 0;
        }
        _world.BeginRead();
        try
        {
            RefreshIfNeeded();
            if (_data.AllRows) return _world.EntityCount != 0;
            for (int i = 0; i < _count; i++)
            {
                QueryMatch match = _matches[i];
                foreach (Chunk chunk in match.Archetype.AllChunks)
                    if (match.Filter is null ? chunk.Count != 0 : match.Filter.Any(chunk, _since)) return true;
            }
            return false;
        }
        finally { _world.EndRead(); }
    }

    public Query Since(ulong version) { _since = version; return this; }

    public Query WithOptionalRead<T>() where T : struct
        => _world.Query(_description.Copy().Optional<T>()).Since(_since);

    internal QueryMatch[] GetMatches(out int count)
    {
        RefreshIfNeeded();
        count = _count;
        return _matches;
    }

    // The caller holds the same structure-read access used to build ranges from these refreshed matches.
    internal bool CoversMatchedTypeLayouts(int type)
    {
        if (!_data.UnmatchedColumns.Has(type)) return true;
        int matched = 0;
        for (int i = 0; i < _world.ArchetypeCount; i++)
        {
            Archetype archetype = _world.Archetypes[i];
            if (matched < _count && ReferenceEquals(_matches[matched].Archetype, archetype)) { matched++; continue; }
            if (!archetype.Types.Bits.Has(type)) continue;
            foreach (Chunk chunk in archetype.AllChunks) if (chunk.Count != 0) return false;
        }
        return true;
    }

    /// <summary>Opens the matched chunks for reading and writing; every write in the scope shares one World version.</summary>
    public QueryScope Open()
    {
        _world.Execution?.EnterQuery(this, [], writeAll: true);
        _world.BeginRead();
        RefreshIfNeeded();
        SparseStore? driver = SparseDriver(out bool none);
        return new QueryScope(_world, _matches, none ? 0 : _count, _since, _world.BeginBatchVersion(0), driver, _data.MatchByArchetype);
    }

    public QueryScope OpenRead()
    {
        _world.Execution?.EnterQuery(this, [], writeAll: false);
        _world.BeginRead();
        RefreshIfNeeded();
        SparseStore? driver = SparseDriver(out bool none);
        return new QueryScope(_world, _matches, none ? 0 : _count, _since, batch: null, driver, _data.MatchByArchetype);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly void RefreshIfNeeded() => _data.RefreshIfNeeded(_world);

    /// <summary>
    /// The smallest required sparse store, when it has fewer members than the matched rows; iterating its members is
    /// then cheaper than filtering every row. <paramref name="none"/> is true when a required store does not exist.
    /// </summary>
    private readonly SparseStore? SparseDriver(out bool none)
    {
        none = false;
        int[] required = _data.Description.Definition.SparseRequiredTypes;
        if (required.Length == 0) return null;
        SparseStore?[] stores = _world.SparseTypes?.Types ?? [];
        SparseStore? smallest = null;
        foreach (int type in required)
        {
            SparseStore? store = (uint)type < (uint)stores.Length ? stores[type] : null;
            if (store is null) { none = true; return null; }
            if (smallest is null || store.Count < smallest.Count) smallest = store;
        }
        int candidates = 0;
        for (int i = 0; i < _count && candidates <= smallest!.Count; i++)
            foreach (Chunk chunk in _matches[i].Archetype.AllChunks) candidates += chunk.Count;
        return smallest!.Count < candidates ? smallest : null;
    }
}

public ref partial struct QueryScope
{
    public ulong SinceVersion => _since;
    private readonly World _world;
    private readonly QueryMatch[] _matches;
    private readonly int _count;
    private readonly ulong _since;
    private readonly World.BatchVersionScope? _batch;
    private readonly SparseStore? _driver;
    private readonly int[] _matchByArchetype;

    internal QueryScope(World world, QueryMatch[] matches, int count, ulong since, World.BatchVersionScope? batch,
        SparseStore? driver = null, int[]? matchByArchetype = null)
        => (_world, _matches, _count, _since, _batch, _driver, _matchByArchetype)
            = (world, matches, count, since, batch, driver, matchByArchetype ?? []);

    public ChunkEnumerator GetEnumerator() => _driver is null
        ? new(_matches, _count, _world.HasMultipleChunks, _since, _batch.HasValue)
        : new(_matches, _since, _batch.HasValue, _driver, _matchByArchetype, _world.Records);
    public void Dispose()
    {
        _batch?.Dispose();
        _world.EndValueRead();
    }
}

public struct ChunkEnumerator
{
    private readonly QueryMatch[] _matches;
    private readonly int _count;
    private readonly bool _multiple;
    private readonly ulong _since;
    private readonly bool _write;
    private int _index;
    private int _chunkIndex;
    private int _nextRow;
    private Chunk? _chunk;
    private QueryRowFilter? _filter;
    private QueryChunk _current;
    private readonly SparseStore? _driver;
    private readonly int[]? _matchByArchetype;
    private readonly EntityRecord[]? _records;

    internal ChunkEnumerator(QueryMatch[] matches, int count, bool multiple, ulong since, bool write)
        => (_matches, _count, _multiple, _since, _write, _index, _chunkIndex, _nextRow, _chunk, _filter, _current)
            = (matches, count, multiple, since, write, -1, 0, 0, null, null, default);

    // Iterates a sparse store's members; each matching entity is a one-row chunk.
    internal ChunkEnumerator(QueryMatch[] matches, ulong since, bool write, SparseStore driver, int[] matchByArchetype,
        EntityRecord[] records) : this(matches, 0, false, since, write)
        => (_driver, _matchByArchetype, _records) = (driver, matchByArchetype, records);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_driver is not null) return MoveNextMember();
        while (true)
        {
            if (_chunk is { } current && _nextRow < current.Count)
            {
                if (_filter is null)
                {
                    _nextRow = current.Count;
                    _current = new QueryChunk(current, 0, current.Count, _write ? ChunkWrites.Published : ChunkWrites.ReadOnly);
                    return true;
                }
                if (_filter.Next(current, _since, ref _nextRow, out int start, out int count))
                {
                    _current = new QueryChunk(current, start, count, _write ? ChunkWrites.Published : ChunkWrites.ReadOnly);
                    return true;
                }
            }
            if (_multiple && _index >= 0)
            {
                ReadOnlySpan<Chunk> chunks = _matches[_index].Archetype.AllChunks;
                if (++_chunkIndex < chunks.Length)
                {
                    _chunk = chunks[_chunkIndex];
                    _nextRow = 0;
                    continue;
                }
            }
            if (++_index >= _count) return false;
            QueryMatch match = _matches[_index];
            _chunk = match.Archetype.PrimaryChunk;
            _chunkIndex = 0;
            _filter = match.Filter;
            _nextRow = 0;
        }
    }

    public readonly QueryChunk Current => _current;

    private bool MoveNextMember()
    {
        SparseStore driver = _driver!;
        while (_nextRow < driver.Count)
        {
            Entities.Entity entity = driver.Entities[_nextRow++];
            ref EntityRecord record = ref _records![entity.Id];
            Chunk chunk = record.Chunk!;
            int archetype = chunk.Archetype.Id;
            int match = (uint)archetype < (uint)_matchByArchetype!.Length ? _matchByArchetype[archetype] - 1 : -1;
            if (match < 0 || !_matches[match].Filter!.Contains(chunk, record.Row, _since)) continue;
            _current = new QueryChunk(chunk, record.Row, 1, _write ? ChunkWrites.Published : ChunkWrites.ReadOnly);
            return true;
        }
        return false;
    }
}

/// <summary>How a chunk view records the writes made through it.</summary>
internal enum ChunkWrites : byte
{
    /// <summary>A read scope; writes are rejected.</summary>
    ReadOnly,
    /// <summary>The current thread checks each access and publishes change summaries immediately.</summary>
    Published,
    /// <summary>A job range stamps rows with the job's version; the runner publishes the summaries after the job.</summary>
    MergedAfterJob,
}

public readonly partial struct QueryChunk
{
    private readonly Chunk _chunk;
    private readonly int _start;
    private readonly ChunkWrites _writes;
    private readonly ulong _jobVersion;
    internal QueryChunk(Chunk chunk, int start, int count, ChunkWrites writes, ulong jobVersion = 0)
        => (_chunk, _start, Count, _writes, _jobVersion) = (chunk, start, count, writes, jobVersion);
    [System.Diagnostics.Conditional("DEBUG")]
    private void RequireWrite()
        => DevelopmentCheck.Require(_writes != ChunkWrites.ReadOnly, "Open a query write scope before changing its values.");
    private bool InJob => _writes == ChunkWrites.MergedAfterJob;
    private ref ComponentChanges ChangesOf(int type) => ref _chunk.Changes![_chunk.Columns[type].ColumnPlusOne - 1];
    public int Count { get; }
    public ReadOnlySpan<SomeEngine.ECS.Entities.Entity> Entities => _chunk.Entities.AsSpan(_start, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> Write<T>() where T : struct
    {
        if (ComponentType<T>.IsSparseType) throw new InvalidOperationException("Write a sparse value by row with Write<T>(row).");
        if (ComponentType<T>.IsSharedType || ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType)
            throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        if (InJob)
        {
            if (ComponentType<T>.TracksWritesType)
                ChangesOf(ComponentType<T>.TypeId).StampRange(_start, Count, _jobVersion, ComponentType<T>.TracksAddedType, publish: false);
        }
        else
        {
            RequireWrite();
            _chunk.Archetype.World.RequireValue<T>(true);
            _chunk.Archetype.World.RequireWriteRange(_chunk, _start, Count);
            if (ComponentType<T>.TracksWritesType)
            {
                ulong version = 0;
                _chunk.Archetype.World.StampRange<T>(_chunk, _start, Count, ref version);
            }
        }
        return ColumnEntry.Span<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _chunk.Count).Slice(_start, Count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> Read<T>() where T : struct
    {
        if (ComponentType<T>.IsSparseType) throw new InvalidOperationException("Read a sparse value by row with Read<T>(row).");
        if (!InJob) _chunk.Archetype.World.RequireValue<T>(false);
        if (ComponentType<T>.IsSharedType) throw new InvalidOperationException("Use ReadShared<T>() to read this range's shared values.");
        return ColumnEntry.Span<T>(ref _chunk.Columns[ComponentType<T>.TypeId], _chunk.Count).Slice(_start, Count);
    }

    public ref readonly T Shared<T>(int row = 0) where T : struct, ISharedComponent
        => ref _chunk.Archetype.World.ReadSharedColumn<T>(_chunk.Columns[ComponentType<T>.TypeId], _start + row);

    public SharedReadOnlySpan<T> ReadShared<T>() where T : struct, ISharedComponent
        => _chunk.Archetype.World.ReadSharedRange<T>(_chunk.Columns[ComponentType<T>.TypeId], _start, Count);

    public int Group<T>() where T : struct, IGroupComponent
        => _chunk.Archetype.TryGroup(ComponentType<T>.TypeId, out int value) ? value
            : throw new InvalidOperationException("Activate GroupBy<T>() before reading the range's group.");

    public ReadOnlyBuffer<T> ReadBuffer<T>(int row) where T : struct
    {
        if (!InJob) _chunk.Archetype.World.RequireValue<Buffer<T>>(false);
        ref Buffer<T> buffer = ref ColumnEntry.At<Buffer<T>>(ref _chunk.Columns[ComponentType<Buffer<T>>.TypeId], _start + row);
        return new(_chunk.Archetype.World, in buffer, false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferRows<T> GetBuffers<T>() where T : struct
    {
        World world = _chunk.Archetype.World;
        if (InJob)
        {
            if (ComponentType<Buffer<T>>.TracksWritesType)
                ChangesOf(ComponentType<Buffer<T>>.TypeId).StampRange(_start, Count, _jobVersion,
                    ComponentType<Buffer<T>>.TracksAddedType, publish: false);
        }
        else
        {
            RequireWrite();
            world.RequireValue<Buffer<T>>(true);
            world.RequireWriteRange(_chunk, _start, Count);
            if (ComponentType<Buffer<T>>.TracksWritesType)
            {
                ulong version = 0;
                world.StampRange<Buffer<T>>(_chunk, _start, Count, ref version);
            }
        }
        Span<Buffer<T>> values = ColumnEntry.Span<Buffer<T>>(
            ref _chunk.Columns[ComponentType<Buffer<T>>.TypeId], _start + Count).Slice(_start, Count);
        return new(world, world.BufferPool<T>(), values);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DynamicBuffer<T> GetBuffer<T>(int row) where T : struct
    {
        World world = _chunk.Archetype.World;
        ref ColumnEntry column = ref _chunk.Columns[ComponentType<Buffer<T>>.TypeId];
        ref Buffer<T> buffer = ref ColumnEntry.At<Buffer<T>>(ref column, _start + row);
        if (InJob)
        {
            if (ComponentType<Buffer<T>>.TracksWritesType)
                _chunk.Changes![column.ColumnPlusOne - 1].StampWord(_start + row, _jobVersion, ComponentType<Buffer<T>>.TracksAddedType);
        }
        else
        {
            RequireWrite();
            world.RequireValue<Buffer<T>>(true);
            world.RequireWriteRow(_chunk, _start + row);
            if (ComponentType<Buffer<T>>.TracksWritesType)
                _chunk.Changes![column.ColumnPlusOne - 1].Stamp(_start + row, world.NextVersion(), false, ComponentType<Buffer<T>>.TracksAddedType);
        }
        if (buffer.Items is null) buffer = world.BufferPool<T>().Rent();
        return new(world, ref buffer, false);
    }
}

public sealed partial class World
{
    public Query Query(QueryDescription description) => new(this, BindQuery(description.Definition), description.Since);
}
