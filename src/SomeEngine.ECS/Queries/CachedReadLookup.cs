using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

[StructLayout(LayoutKind.Sequential)]
internal struct CachedReference<T> where T : struct
{
    internal T[]? Values;
    internal int Row;
    internal int Generation;
}

internal abstract class CachedComponent(int typeId)
{
    internal readonly int TypeId = typeId;
    internal abstract void Reserve(int capacity);
    internal abstract void Bind(Entity entity, Chunk chunk, int row);
    internal abstract void BindRange(Chunk chunk, int start, int count);
    internal abstract void Remove(Entity entity);
    internal abstract void RemoveRange(ReadOnlySpan<Entity> entities);
    internal abstract void Clear();
    internal abstract void ReleaseData();
}

internal sealed class CachedComponent<T> : CachedComponent where T : struct
{
    internal int[]? Generations;
    internal int[]? ByteOffsets;
    internal CachedReference<T>[]? References;
    internal readonly PodColumnBuffer? Buffer;
    internal bool RowsFollowIds;

    internal CachedComponent(World world, int capacity) : base(ComponentType<T>.TypeId)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) References = new CachedReference<T>[capacity];
        else
        {
            if (!ComponentType<T>.IsTagType) Buffer = world.PodBuffer(ComponentType<T>.Value);
            RowsFollowIds = Buffer is not null;
            ByteOffsets = ArrayPool<int>.Shared.Rent(capacity);
            Generations = ArrayPool<int>.Shared.Rent(capacity);
            Array.Clear(Generations);
        }
    }

    internal override void Reserve(int capacity)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Resize(ref References, capacity);
        else
        {
            int[] generations = ArrayPool<int>.Shared.Rent(capacity);
            Generations!.AsSpan().CopyTo(generations);
            generations.AsSpan(Generations!.Length).Clear();
            int[] byteOffsets = ArrayPool<int>.Shared.Rent(capacity);
            ByteOffsets!.AsSpan().CopyTo(byteOffsets);
            ArrayPool<int>.Shared.Return(Generations);
            ArrayPool<int>.Shared.Return(ByteOffsets!);
            Generations = generations;
            ByteOffsets = byteOffsets;
        }
    }

    internal override void Bind(Entity entity, Chunk chunk, int row)
    {
        ref ColumnEntry column = ref chunk.Columns[TypeId];
        if (!column.Has(row)) { Remove(entity); return; }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            References![entity.Id] = new() { Values = Unsafe.As<T[]>(column.Values!), Row = row, Generation = entity.Generation };
        else
            BindByteOffset(entity, (int)(column.Rows - (Buffer?.Rows ?? 0)) + row * Unsafe.SizeOf<T>());
    }

    internal override void BindRange(Chunk chunk, int start, int count)
    {
        ref ColumnEntry column = ref chunk.Columns[TypeId];
        Entity[] entities = chunk.Entities;
        int end = start + count;
        if (column.Presence is not null)
        {
            for (int row = start; row < end; row++) Bind(entities[row], chunk, row);
            return;
        }
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            T[] values = Unsafe.As<T[]>(column.Values!);
            CachedReference<T>[] references = References!;
            for (int row = start; row < end; row++)
            {
                Entity entity = entities[row];
                references[entity.Id] = new() { Values = values, Row = row, Generation = entity.Generation };
            }
        }
        else
        {
            int offset = (int)(column.Rows - (Buffer?.Rows ?? 0)) + start * Unsafe.SizeOf<T>();
            int[] generations = Generations!;
            int[] byteOffsets = ByteOffsets!;
            int row = start;
            if (RowsFollowIds)
                for (; row < end; row++, offset += Unsafe.SizeOf<T>())
                {
                    Entity entity = entities[row];
                    if (offset != (entity.Id - 1) * Unsafe.SizeOf<T>()) { RowsFollowIds = false; break; }
                    byteOffsets[entity.Id] = offset; generations[entity.Id] = entity.Generation;
                }
            for (; row < end; row++, offset += Unsafe.SizeOf<T>())
            {
                Entity entity = entities[row];
                byteOffsets[entity.Id] = offset; generations[entity.Id] = entity.Generation;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BindByteOffset(Entity entity, int offset)
    {
        ByteOffsets![entity.Id] = offset;
        Generations![entity.Id] = entity.Generation;
        if (RowsFollowIds && offset != (entity.Id - 1) * Unsafe.SizeOf<T>()) RowsFollowIds = false;
    }

    internal override void Remove(Entity entity)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) References![entity.Id] = default;
        else
        {
            Generations![entity.Id] = 0;
        }
    }

    internal override void RemoveRange(ReadOnlySpan<Entity> entities)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            CachedReference<T>[] references = References!;
            foreach (Entity entity in entities) references[entity.Id] = default;
        }
        else
        {
            int[] generations = Generations!;
            foreach (Entity entity in entities) generations[entity.Id] = 0;
        }
    }

    internal override void Clear()
    {
        RowsFollowIds = Buffer is not null;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(References!);
        else
        {
            Array.Clear(Generations!);
        }
    }

    internal override void ReleaseData()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) References = [];
        else
        {
            ArrayPool<int>.Shared.Return(Generations!);
            ArrayPool<int>.Shared.Return(ByteOffsets!);
            Generations = ByteOffsets = [];
        }
        RowsFollowIds = false;
    }
}

public unsafe ref struct CachedReadLookup<T> where T : struct
{
    private readonly World _world;
    private readonly int _used;
    private readonly bool _rowsFollowIds;
    private readonly bool _release;
    private readonly ref int _firstGeneration;
    private readonly ref T _firstValue;
    private readonly ref int _firstByteOffset;
    private readonly ref CachedReference<T> _firstReference;

    internal World World => _world;

    internal CachedReadLookup(World world)
    {
        _world = world;
        _used = world.Used;
        _release = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CachedReadLookup(World world, CachedComponent<T> data, bool release)
    {
        _world = world;
        _used = world.Used;
        _rowsFollowIds = data.RowsFollowIds;
        _release = release;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            _firstReference = ref MemoryMarshal.GetArrayDataReference(data.References!);
        else
        {
            _firstValue = ref Unsafe.AsRef<T>((void*)(data.Buffer?.Rows ?? 0));
            _firstByteOffset = ref MemoryMarshal.GetArrayDataReference(data.ByteOffsets!);
            _firstGeneration = ref MemoryMarshal.GetArrayDataReference(data.Generations!);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read(Entity entity)
        => ref Access(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Access(Entity entity, ulong writeVersion = 0, bool write = false)
    {
        if ((uint)(entity.Id - 1) >= (uint)_used) throw new ArgumentException("Entity id is outside this world.");
        nint index = (nint)(uint)entity.Id;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            ref CachedReference<T> entry = ref Unsafe.Add(ref _firstReference, index);
            if (entry.Generation != entity.Generation) throw new ArgumentException("Entity component is absent.");
            if (write) _world.RequireWriteEntity(entity.Id);
            if (writeVersion != 0) Stamp(entity.Id, writeVersion);
            return ref entry.Values![entry.Row];
        }
        else
        {
            if (Unsafe.Add(ref _firstGeneration, index) != entity.Generation)
                throw new ArgumentException("Entity component is absent.");
            if (write) _world.RequireWriteEntity(entity.Id);
            if (writeVersion != 0) Stamp(entity.Id, writeVersion);
            nint offset = (nint)(uint)Unsafe.Add(ref _firstByteOffset, index);
            return ref Unsafe.AddByteOffset(ref _firstValue, offset);
        }
    }

    /// <summary>Reads a component whose identity and presence are already established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T ReadKnown(Entity entity) => ref AccessKnown(entity.Id);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T AccessKnown(int entityId, ulong writeVersion = 0, bool write = false)
    {
        if (write) _world.RequireWriteEntity(entityId);
        if (writeVersion != 0) Stamp(entityId, writeVersion);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            ref CachedReference<T> entry = ref Unsafe.Add(ref _firstReference, (nint)(uint)entityId);
            return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entry.Values!), (nint)(uint)entry.Row);
        }
        nint offset = (nint)(uint)Unsafe.Add(ref _firstByteOffset, (nint)(uint)entityId);
        return ref Unsafe.AddByteOffset(ref _firstValue, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(Entity entity)
    {
        if ((uint)(entity.Id - 1) >= (uint)_used) return false;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return Unsafe.Add(ref _firstReference, (nint)(uint)entity.Id).Generation == entity.Generation;
        return Unsafe.Add(ref _firstGeneration, (nint)(uint)entity.Id) == entity.Generation;
    }

    internal void SetKnown(ReadOnlySpan<Entity> entities, in T value, ulong writeVersion)
    {
        if (_world.HasRangeWrite)
            foreach (Entity entity in entities) _world.RequireWriteEntity(entity.Id);
        T repeated = value;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() || writeVersion != 0)
        {
            foreach (Entity entity in entities) AccessKnown(entity.Id, writeVersion) = repeated;
            return;
        }
        ref T first = ref _firstValue;
        ref Entity identity = ref MemoryMarshal.GetReference(entities);
        if (_rowsFollowIds)
        {
            int row = 0, end = entities.Length - 4;
            for (; row <= end; row += 4)
            {
                Unsafe.Add(ref first, Unsafe.Add(ref identity, row).Id - 1) = repeated;
                Unsafe.Add(ref first, Unsafe.Add(ref identity, row + 1).Id - 1) = repeated;
                Unsafe.Add(ref first, Unsafe.Add(ref identity, row + 2).Id - 1) = repeated;
                Unsafe.Add(ref first, Unsafe.Add(ref identity, row + 3).Id - 1) = repeated;
            }
            for (; row < entities.Length; row++) Unsafe.Add(ref first, Unsafe.Add(ref identity, row).Id - 1) = repeated;
            return;
        }
        ref int offsets = ref _firstByteOffset;
        int i = 0;
        int last = entities.Length - 4;
        for (; i <= last; i += 4)
        {
            Unsafe.AddByteOffset(ref first, (nint)(uint)Unsafe.Add(ref offsets, Unsafe.Add(ref identity, i).Id)) = repeated;
            Unsafe.AddByteOffset(ref first, (nint)(uint)Unsafe.Add(ref offsets, Unsafe.Add(ref identity, i + 1).Id)) = repeated;
            Unsafe.AddByteOffset(ref first, (nint)(uint)Unsafe.Add(ref offsets, Unsafe.Add(ref identity, i + 2).Id)) = repeated;
            Unsafe.AddByteOffset(ref first, (nint)(uint)Unsafe.Add(ref offsets, Unsafe.Add(ref identity, i + 3).Id)) = repeated;
        }
        for (; i < entities.Length; i++)
            Unsafe.AddByteOffset(ref first, (nint)(uint)Unsafe.Add(ref offsets, Unsafe.Add(ref identity, i).Id)) = repeated;
    }

    public void Dispose() { if (_release) _world.EndValueRead(); }

    internal ulong WriteVersion(ref ulong version) => _world.WriteVersion(ref version);

    private void Stamp(int entityId, ulong version)
    {
        ref EntityRecord record = ref _world.Records[entityId];
        World.Stamp<T>(record.Chunk!, record.Row, version);
    }
}

public ref struct CachedWriteLookup<T> where T : struct
{
    private CachedReadLookup<T> _read;
    private ulong _version;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CachedWriteLookup(CachedReadLookup<T> read) => _read = read;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get(Entity entity)
        => ref _read.Access(entity, ComponentType<T>.TracksWritesType ? _read.WriteVersion(ref _version) : 0, true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(Entity entity, in T value) => Get(entity) = value;

    /// <summary>Gets a component whose identity and presence are already established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetKnown(Entity entity)
        => ref _read.AccessKnown(entity.Id, ComponentType<T>.TracksWritesType ? _read.WriteVersion(ref _version) : 0, true);

    public void SetKnown(ReadOnlySpan<Entity> entities, in T value)
        => _read.SetKnown(entities, value, ComponentType<T>.TracksWritesType && !entities.IsEmpty ? _read.WriteVersion(ref _version) : 0);

    public bool Has(Entity entity) => _read.Has(entity);
    public void Dispose() => _read.Dispose();
}

public sealed partial class World
{
    private CachedComponent?[] _cachedByType = [];
    private readonly List<CachedComponent> _cachedTypes = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CachedWriteLookup<T> OpenCachedWrite<T>() where T : struct
    {
        if (ComponentType<T>.IsControlledType || ComponentType<T>.IsIndexedType || ComponentType<T>.IsGroupedType) throw new InvalidOperationException("Use the component's typed write view or Set entry.");
        return new(OpenCached<T>(true));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CachedReadLookup<T> OpenCachedRead<T>() where T : struct
        => OpenCached<T>(false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal CachedReadLookup<T> OpenCached<T>(bool write) where T : struct
    {
        CachedComponent<T> data = GetCachedComponent<T>(write, out bool release);
        return new(this, data, release);
    }

    private CachedComponent<T> GetCachedComponent<T>(bool write, out bool release) where T : struct
    {
        if (ComponentType<T>.IsSharedType) throw new InvalidOperationException("Use the shared component read entry.");
        release = write || !HasRangeWrite;
        if (release) BeginValueRead<T>(write);
        else RequireValue<T>(false);
        int type = ComponentType<T>.TypeId;
        CachedComponent<T> data;
        if ((uint)type < (uint)_cachedByType.Length && _cachedByType[type] is { } found)
            data = (CachedComponent<T>)found;
        else
        {
            lock (LazyInitializer.EnsureInitialized(ref _queryBindingsLock))
                data = (uint)type < (uint)_cachedByType.Length && _cachedByType[type] is { } existing
                    ? (CachedComponent<T>)existing : CreateCachedComponent<T>();
        }
        return data;
    }

    private CachedComponent<T> CreateCachedComponent<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        var data = new CachedComponent<T>(this, Records.Length);
        if (type >= _cachedByType.Length) Array.Resize(ref _cachedByType, Math.Max(type + 1, _cachedByType.Length * 2));
        for (int i = 0; i < ArchetypeCount; i++)
        {
            Chunk chunk = Archetypes[i].PrimaryChunk;
            if ((uint)type < (uint)chunk.Columns.Length && chunk.Columns[type].ColumnPlusOne != 0)
            {
                chunk.Archetype.AddCachedComponent(data);
                foreach (Chunk current in chunk.Archetype.AllChunks) data.BindRange(current, 0, current.Count);
            }
        }
        _cachedByType[type] = data;
        _cachedTypes.Add(data);
        return data;
    }

    private void RegisterCachedComponents(Archetype archetype)
    {
        foreach (int type in archetype.Types.Types)
        {
            if ((uint)type < (uint)_cachedByType.Length && _cachedByType[type] is { } data)
                archetype.AddCachedComponent(data);
            if ((uint)type < (uint)_podBuffers.Length && _podBuffers[type] is { IsObserved: true } buffer)
                archetype.AddCachedComponent(buffer);
        }
    }
}
