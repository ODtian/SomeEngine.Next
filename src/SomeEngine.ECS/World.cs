using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS;

public sealed partial class World : IDisposable
{
    internal EntityRecord[] Records;
    internal int Used;
    private int _free;
    private int _count;
    private int _readers;
    private bool _disposed;
    private bool _hasRetainedTypes;
    private bool _hasOwnedValues;
    private readonly Dictionary<TypeSet, Archetype> _types = new();
    private Archetype?[] _combinations = [];
    internal Archetype[] Archetypes = new Archetype[4];
    internal int ArchetypeCount;
    internal int QueryVersion;
    internal int QueryMatchVersion;
    private readonly bool _retainRemovedColumns;
    public int ChunkCapacity { get; }
    internal bool HasMultipleChunks;
    private static readonly TypeSet EmptyTypes = new();

    public World(int capacity = 16, bool retainRemovedColumns = true, int chunkCapacity = 0, WorldExecution? execution = null)
    {
        Records = new EntityRecord[capacity + 1];
        _retainRemovedColumns = retainRemovedColumns;
        ChunkCapacity = chunkCapacity;
        Execution = execution;
        execution?.Attach(this);
    }

    public int EntityCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireCallingThread();
            Execution?.CheckActive();
            return _count;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref EntityRecord Resolve(Entity entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireCallingThread();
        if ((uint)(entity.Id - 1) >= (uint)Used) throw new ArgumentException("Entity id is outside this world.");
        ref EntityRecord record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), entity.Id);
        if (record.Generation != entity.Generation) throw new ArgumentException("Entity generation has expired.");
        return ref record;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity entity)
    {
        if (Execution is not null) return IsAliveWithExecution(entity);
        return IsAliveCore(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsAliveCore(Entity entity)
    {
        return (uint)(entity.Id - 1) < (uint)Used && Records[entity.Id].Generation == entity.Generation
            && Records[entity.Id].Chunk is not null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read<T>(Entity entity) where T : struct
    {
        if (ComponentType<T>.IsSparseType) return ref ReadSparseValue<T>(entity);
        RequireValue<T>(false);
        if (ComponentType<T>.IsSharedType) return ref ReadSharedValue<T>(entity);
        return ref Access<T>(entity, false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(Entity entity) where T : struct
    {
        if (ComponentType<T>.IsSparseType) return ref GetSparseValue<T>(entity);
        RequireValue<T>(true);
        if (ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        return ref Access<T>(entity, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref T Access<T>(Entity entity, bool write) where T : struct
    {
        ref EntityRecord record = ref Resolve(entity);
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)columns.Length || !columns[type].Has(record.Row))
            throw new ArgumentException("Entity does not have this component.");
        if (write) RequireWriteRow(record.Chunk, record.Row);
        if (write && ComponentType<T>.TracksWritesType) Stamp<T>(record.Chunk, record.Row, NextVersion());
        return ref ColumnEntry.At<T>(ref columns[type], record.Row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>(Entity entity) where T : struct
    {
        if (ComponentType<T>.IsSparseType) return HasSparseValue<T>(entity);
        if (Execution is not null) return HasWithExecution<T>(entity);
        return HasCore<T>(entity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasCore<T>(Entity entity) where T : struct
    {
        RequireCallingThread();
        if (!IsAliveCore(entity)) return false;
        ref EntityRecord record = ref Records[entity.Id];
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)columns.Length && columns[type].Has(record.Row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsSparseType) { SetSparseValue(entity, value); return; }
        if (Execution is not null) { SetWithExecution(entity, value); return; }
        SetComponent(entity, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetComponent<T>(Entity entity, in T value) where T : struct
    {
        ComponentValues.Bind<T>();
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        using MembershipEdit membership = BeginMembershipEdit(entity);
        if (ComponentType<T>.IsIndexedType && ActiveIndex<T>() is { } index) { index.Set(entity, value); return; }
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping) { grouping.Set(entity, value); return; }
        SetValue(entity, value);
    }

    private static void RequireActiveWrite<T>(in EntityRecord record) where T : struct
    {
        if (!ComponentType<T>.IsCleanupType && HasDestroyed(in record))
            throw new InvalidOperationException("Write ordinary component values on an active entity.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetValue<T>(Entity entity, in T value) where T : struct
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        if (ComponentType<T>.IsSharedType) { SetSharedValue(entity, value); return; }
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        ref EntityRecord record = ref Resolve(entity);
        SetValueKnown(entity, ref record, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetValueKnown<T>(Entity entity, ref EntityRecord record, in T value) where T : struct
    {
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<T>.TypeId;
        if ((uint)type < (uint)columns.Length && columns[type].Has(record.Row))
            ReplacePresentValue(record.Chunk, record.Row, value);
        else AddKnown(entity, ref record, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReplacePresentValue<T>(Chunk chunk, int row, in T value, ulong version = 0) where T : struct
    {
        RequireWriteRow(chunk, row);
        ref ColumnEntry column = ref chunk.Columns[ComponentType<T>.TypeId];
        if (ComponentType<T>.HasValueOperationsType) ComponentType<T>.ReleaseValue(this, ref ColumnEntry.At<T>(ref column, row));
        if (!ComponentType<T>.IsTagType) ColumnEntry.At<T>(ref column, row) = value;
        if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, row, version != 0 ? version : NextVersion());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireStructure()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RequireCallingThread();
        if (_readers != 0) throw new InvalidOperationException("Finish the current read scope before changing entity layout.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequireCallingThread()
    {
        if (Execution is null && JobThreads.IsActive)
            throw new InvalidOperationException("Connect WorldJobs before accessing World from a Job.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Archetype Find(TypeSet types)
    {
        Archetype?[] combinations = _combinations;
        if (types.Id != 0 && (uint)types.Id < (uint)combinations.Length && combinations[types.Id] is { } found)
            return found;
        return Bind(types);
    }

    private Archetype Bind(TypeSet types)
    {
        RequireStructure();
        if (!_types.TryGetValue(types, out Archetype? archetype))
        {
            archetype = new Archetype(this, ArchetypeCount, types, 16);
            _hasRetainedTypes |= archetype.RetainsOnDestroy;
            _hasOwnedValues |= archetype.OwnedValues.Length != 0;
            RegisterCachedComponents(archetype);
            _types.Add(types, archetype);
            if (ArchetypeCount == Archetypes.Length) Array.Resize(ref Archetypes, ArchetypeCount * 2);
            Archetypes[ArchetypeCount++] = archetype;
            RefreshQueryMatches();
        }
        if (types.Id == 0) return archetype;
        if (types.Id >= _combinations.Length)
            Array.Resize(ref _combinations, Math.Max(types.Id + 1, Math.Max(16, _combinations.Length * 2)));
        _combinations[types.Id] = archetype;
        return archetype;
    }

    internal void ReserveEntities(int needed)
    {
        if (needed >= Records.Length)
        {
            Array.Resize(ref Records, Math.Max(needed + 1, Records.Length * 2));
            foreach (CachedComponent data in _cachedTypes) data.Reserve(Records.Length);
            foreach (IValueIndex? index in _valueIndexes) index?.Reserve(Records.Length);
            foreach (Shared.SharedValues? values in _shared) values?.Groups.ReserveEntities(Records.Length);
            _sparse?.ReserveEntities(Records.Length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateKnown(ref Chunk chunk, ulong version = 0)
    {
        RequireStructure();
        chunk = chunk.EnsureRow();
        int id, generation;
        if (_free != 0)
        {
            id = _free;
            ref EntityRecord free = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), id);
            _free = free.Row;
            generation = free.Generation;
        }
        else
        {
            id = Used + 1;
            if (id >= Records.Length) ReserveEntities(id);
            Used = id;
            generation = 1;
        }
        int row = chunk.Count++;
        Entity entity = new(id, generation);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), id) = new(chunk, row, generation);
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(chunk.Entities), row) = entity;
        if ((chunk.Archetype.TransferKinds & 4) != 0) chunk.InitializePresence(row);
        if (chunk.Changes is not null) StampCreated(chunk, row, 1, version);
        if (chunk.Archetype.Cached is { } cached)
            foreach (CachedComponent data in cached) data.Bind(entity, chunk, row);
        _count++;
        return entity;
    }

    public Entity Create()
    {
        if (Execution is not null) return CreateWithExecution();
        return CreateCore();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore()
    {
        Chunk chunk = Find(EmptyTypes).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        return entity;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Create<T>(in T value) where T : struct
    {
        if (Execution is not null) return CreateWithExecution(value);
        return CreateCore(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Entity CreateCore<T>(in T value) where T : struct
    {
        if (ComponentType<T>.HasInitializerType) ComponentType<T>.ValidateCreate(this, value);
        Chunk chunk = Find(TypeSet<T>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        WriteCreatedValue(entity, chunk, chunk.Count - 1, value);
        if (ComponentType<T>.IsIndexedType) IndexCreated(entity, value);
        if (ComponentType<T>.IsGroupedType) ArrangeCreatedGroups(entity);
        if (ComponentType<T>.HasInitializerType) ComponentType<T>.Initialize(this, entity);
        return entity;
    }

    /// <summary>Creates a relationship edge entity with its endpoints and payload in one structural move.</summary>
    internal Entity CreateEdge<TEndpoints, TPayload>(in TEndpoints endpoints, in TPayload payload)
        where TEndpoints : struct where TPayload : struct
    {
        using WorldAccess access = OpenStructure();
        if (ComponentType<TEndpoints>.HasInitializerType) ComponentType<TEndpoints>.ValidateCreate(this, endpoints);
        if (ComponentType<TPayload>.HasInitializerType) ComponentType<TPayload>.ValidateCreate(this, payload);
        Chunk chunk = Find(TypeSet<TEndpoints, TPayload>.Value).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        int row = chunk.Count - 1;
        WriteCreatedValue(entity, chunk, row, endpoints);
        WriteCreatedValue(entity, chunk, row, payload);
        if (ComponentType<TEndpoints>.IsIndexedType) IndexCreated(entity, endpoints);
        if (ComponentType<TPayload>.IsIndexedType) IndexCreated(entity, payload);
        if (ComponentType<TEndpoints>.IsGroupedType || ComponentType<TPayload>.IsGroupedType) ArrangeCreatedGroups(entity);
        if (ComponentType<TEndpoints>.HasInitializerType) ComponentType<TEndpoints>.Initialize(this, entity);
        if (ComponentType<TPayload>.HasInitializerType) ComponentType<TPayload>.Initialize(this, entity);
        return entity;
    }

    public void Reserve<T>(int capacity) where T : struct
        => ReserveTypes(TypeSet<T>.Value, capacity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteCreatedValue<T>(Entity entity, Chunk chunk, int row, in T value) where T : struct
    {
        if (ComponentType<T>.IsSharedType)
        {
            var values = Shared<T>();
            int id = values.Intern(value);
            ColumnEntry.At<int>(ref chunk.Columns[ComponentType<T>.TypeId], row) = id;
            values.Groups.Set(entity, -1, id, true);
        }
        else if (!ComponentType<T>.IsTagType)
            ColumnEntry.At<T>(ref chunk.Columns[ComponentType<T>.TypeId], row) =
                ComponentType<T>.HasValueOperationsType ? ComponentType<T>.CloneValue(this, value) : value;
    }


    internal void BeginRead()
        => BeginRead(Execution is not null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginRead(bool withExecution)
    {
        RequireCallingThread();
        if (!withExecution) _readers++;
        else Interlocked.Increment(ref _readers);
    }
    internal void EndRead()
        => EndRead(Execution is not null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndRead(bool withExecution)
    {
        if (!withExecution) _readers--;
        else Interlocked.Decrement(ref _readers);
    }

    public void Clear()
    {
        Execution?.BeginClear();
        try
        {
            using WorldAccess access = OpenStructure();
            ClearValues();
        }
        finally { Execution?.EndClear(); }
    }

    internal void ClearValues()
    {
        RequireStructure();
        DataGeneration++;
        ClearLifecycle();
        ReserveBufferReturns();
        ReleaseOwnedValues();
        ReleaseResources();
        ClearIndexes();
        foreach (Shared.SharedValues? values in _shared) values?.Groups.Clear();
        _sparse?.Clear(Used);
        for (int i = 0; i < ArchetypeCount; i++)
        {
            Archetype archetype = Archetypes[i];
            foreach (Chunk chunk in archetype.AllChunks)
            {
                foreach (ComponentType type in archetype.Managed)
                    if (!type.HasColumnRelease || chunk.Columns[type.Id].Presence is not null)
                        Array.Clear(chunk.Columns[type.Id].Values!, 0, chunk.Count);
                chunk.Count = 0;
                if (chunk.DisabledCounts is { } disabled) Array.Clear(disabled);
                if (chunk.Changes is { } changes)
                    foreach (ComponentType type in archetype.Tracked)
                    {
                        ref ComponentChanges values = ref changes[chunk.Columns[type.Id].ColumnPlusOne - 1];
                        Array.Clear(values.Rows);
                        Array.Clear(values.Words);
                        Array.Clear(values.Blocks);
                        values.LastWrite = 0;
                    }
            }
            archetype.ResetWritableChunks();
        }
        int free = 0;
        ref EntityRecord firstRecord = ref MemoryMarshal.GetArrayDataReference(Records);
        for (int id = Used; id > 0; id--)
        {
            ref EntityRecord record = ref Unsafe.Add(ref firstRecord, id);
            record = new(null, free, record.Generation + 1);
            free = id;
        }
        _free = free;
        _count = 0;
        foreach (CachedComponent data in _cachedTypes) data.Clear();
    }

    public void ValidateDisposePreconditions()
    {
        if (_disposed) return;
        RequireStructure();
        Execution?.ValidateDisposePreconditions();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Execution?.BeginDispose();
        using (WorldAccess access = OpenStructure()) DisposeValues();
        DisposeEventLogs();
        Execution?.Release();
    }

    private void DisposeValues()
    {
        RequireStructure();
        RecyclesBuffers = false;
        DataGeneration++;
        DisposeLifecycle();
        ReleaseQueryBindings();
        ReleaseOwnedValues();
        ReleaseResources();
        ClearIndexes();
        _types.Clear();
        _copies = null;
        _sparse?.ReleaseData();
        foreach (CachedComponent? data in _cachedByType) data?.ReleaseData();
        Records = [];
        Archetypes = [];
        _combinations = [];
        _cachedByType = [];
        _cachedTypes.Clear();
        _sparse = null;
        _bufferPools = [];
        foreach (PodColumnBuffer? buffer in _podBuffers) buffer?.Release();
        _podBuffers = [];
        _resources = default;
        _valueIndexes = [];
        _groupings = [];
        _groupedTypes = default;
        _groupTables.Clear();
        foreach (Shared.SharedValues? values in _shared) values?.Release(this);
        _shared = [];
        Used = _count = _free = ArchetypeCount = 0;
        HasMultipleChunks = false;
        _disposed = true;
    }

    internal bool RecyclesBuffers = true;

    private void ReleaseOwnedValues()
    {
        if (!_hasOwnedValues) return;
        for (int i = 0; i < ArchetypeCount; i++)
        {
            Archetype archetype = Archetypes[i];
            foreach (Chunk chunk in archetype.AllChunks)
            {
                if (chunk.Count == 0) continue;
                foreach (ComponentType type in archetype.OwnedValues)
                    type.ReleaseRows(this, ref chunk.Columns[type.Id], 0, chunk.Count);
            }
        }
    }
}
