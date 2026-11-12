using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public struct Buffer<T> : IEnableableComponent, IComponentOperations<Buffer<T>>, IComponentColumnOperations<Buffer<T>>, IBufferJobElements, IEntityReferenceOperations<Buffer<T>> where T : struct
{
    bool IBufferJobElements.IsAliasFree => Registry.JobStorageTypeMetadata<T>.IsAliasFree;
    Buffer<T> IComponentOperations<Buffer<T>>.Clone(World world, in Buffer<T> value)
    {
        if (value.Items is null) return default;
        Buffer<T> copy = world.BufferPool<T>().Rent();
        copy.Reserve(value.Count);
        value.Items.AsSpan(0, value.Count).CopyTo(copy.Items);
        copy.Count = value.Count;
        return copy;
    }
    void IComponentOperations<Buffer<T>>.Release(World world, ref Buffer<T> value)
    {
        if (value.Items is not null && world.RecyclesBuffers) world.BufferPool<T>().Return(value);
        value = default;
    }
    void IComponentColumnOperations<Buffer<T>>.Release(World world, Span<Buffer<T>> values)
    {
        if (world.RecyclesBuffers) world.BufferPool<T>().Return(values);
        values.Clear();
    }
    void IEntityReferenceOperations<Buffer<T>>.Remap(ref Buffer<T> value, in EntityMap entities)
    {
        for (int index = 0; index < value.Count; index++) ComponentValues.Remap(ref value.Items![index], entities);
    }

    internal T[]? Items;
    internal int Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Reserve(int needed)
    {
        if (needed > Items!.Length) Grow(needed);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int needed)
    {
        T[] previous = Items!;
        int capacity = Math.Max(needed, Math.Max(4, previous.Length * 2));
        var items = new T[capacity];
        previous.AsSpan(0, Count).CopyTo(items);
        Items = items;
    }

    internal void Clear()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Count != 0) Array.Clear(Items!, 0, Count);
        Count = 0;
    }
}

internal interface IBufferPool
{
    void ReserveReturns(World world);
}

internal sealed class BufferPool<T> : IBufferPool where T : struct
{
    private Buffer<T>[] _free = [];
    private int _count;

    void IBufferPool.ReserveReturns(World world)
    {
        int type = ComponentType<Buffer<T>>.TypeId;
        int removed = ComponentType<Buffer<T>>.KeepsRemovedType ? ComponentType<Removed<Buffer<T>>>.TypeId : -1;
        int count = 0;
        for (int i = 0; i < world.ArchetypeCount; i++)
        {
            Archetype archetype = world.Archetypes[i];
            int copies = archetype.Types.Bits.Has(type) ? 1 : 0;
            if (removed >= 0 && archetype.Types.Bits.Has(removed)) copies++;
            if (copies != 0)
                foreach (Chunk chunk in archetype.AllChunks) count += copies * chunk.Count;
        }
        int needed = _count + count;
        if (needed > _free.Length) Array.Resize(ref _free, needed);
    }

    internal Buffer<T> Rent()
    {
        if (_count == 0) return new() { Items = [] };
        Buffer<T> data = _free[--_count];
        _free[_count] = default;
        return data;
    }

    internal void Return(Buffer<T> data)
    {
        data.Clear();
        if ((long)data.Items!.Length * Unsafe.SizeOf<T>() > 4096) return;
        if (_count == _free.Length) Array.Resize(ref _free, Math.Max(4, _count * 2));
        _free[_count++] = data;
    }

    internal void Return(Span<Buffer<T>> values)
    {
        int returned = 0;
        foreach (ref Buffer<T> value in values)
            if (value.Items is { } items)
            {
                value.Clear();
                if ((long)items.Length * Unsafe.SizeOf<T>() <= 4096) returned++;
                else value = default;
            }
        int needed = _count + returned;
        if (needed > _free.Length)
            Array.Resize(ref _free, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(4, needed)));
        foreach (ref readonly Buffer<T> value in values)
            if (value.Items is not null) _free[_count++] = value;
    }
}

internal interface IBufferJobElements
{
    bool IsAliasFree { get; }
}

public readonly ref struct ReadOnlyBuffer<T> where T : struct
{
    private readonly World _world;
    private readonly ref readonly Buffer<T> _data;
    private readonly bool _release;
    internal ReadOnlyBuffer(World world, in Buffer<T> data, bool release)
    {
        _world = world; _data = ref data; _release = release;
    }
    public int Count => _data.Count;
    public ReadOnlySpan<T> AsSpan() => _data.Items.AsSpan(0, _data.Count);
    public ref readonly T this[int index] => ref AsSpan()[index];
    public void Dispose() { if (_release) _world.EndValueRead(); }
}

public readonly ref struct BufferRows<T> where T : struct
{
    private readonly World _world;
    private readonly BufferPool<T> _pool;
    private readonly Span<Buffer<T>> _values;

    internal BufferRows(World world, BufferPool<T> pool, Span<Buffer<T>> values)
    {
        _world = world;
        _pool = pool;
        _values = values;
    }

    public int Count => _values.Length;
    public DynamicBuffer<T> this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref Buffer<T> buffer = ref _values[row];
            if (buffer.Items is null) buffer = _pool.Rent();
            return new(_world, ref buffer, false);
        }
    }
}

public readonly ref struct DynamicBuffer<T> where T : struct
{
    private readonly World _world;
    private readonly ref Buffer<T> _data;
    private readonly bool _release;
    internal DynamicBuffer(World world, ref Buffer<T> data, bool release)
    {
        _world = world; _data = ref data; _release = release;
    }
    public int Count => _data.Count;
    public int Capacity => _data.Items!.Length;
    public Span<T> AsSpan() => _data.Items.AsSpan(0, _data.Count);
    public ref T this[int index] => ref AsSpan()[index];

    public void EnsureCapacity(int capacity) => _data.Reserve(capacity);
    public void Add(in T value)
    {
        int row = _data.Count;
        _data.Reserve(row + 1);
        _data.Items![row] = value;
        _data.Count = row + 1;
    }
    public void AddRange(ReadOnlySpan<T> values)
    {
        int start = _data.Count;
        _data.Reserve(start + values.Length);
        values.CopyTo(_data.Items.AsSpan(start));
        _data.Count = start + values.Length;
    }
    public void Insert(int index, in T value)
    {
        int count = _data.Count;
        _data.Reserve(count + 1);
        T[] items = _data.Items!;
        items.AsSpan(index, count - index).CopyTo(items.AsSpan(index + 1));
        items[index] = value;
        _data.Count = count + 1;
    }
    public void RemoveAt(int index)
    {
        int last = _data.Count - 1;
        T[] items = _data.Items!;
        items.AsSpan(index + 1, last - index).CopyTo(items.AsSpan(index));
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) items[last] = default;
        _data.Count = last;
    }
    public void RemoveAtSwapBack(int index)
    {
        Span<T> values = AsSpan();
        values[index] = values[^1];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) values[^1] = default;
        _data.Count--;
    }
    public void Resize(int count)
    {
        _data.Reserve(count);
        if (count > _data.Count) Array.Clear(_data.Items!, _data.Count, count - _data.Count);
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_data.Items!, count, _data.Count - count);
        _data.Count = count;
    }
    public void Clear() => _data.Clear();
    public void TrimExcess() => Array.Resize(ref _data.Items, _data.Count);
    public void Dispose() { if (_release) _world.EndValueRead(); }
}

public sealed partial class World
{
    private object?[] _bufferPools = [];

    private void ReserveBufferReturns()
    {
        foreach (object? entry in _bufferPools)
            if (entry is IBufferPool pool) pool.ReserveReturns(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal BufferPool<T> BufferPool<T>() where T : struct
    {
        int type = ComponentType<Buffer<T>>.TypeId;
        if ((uint)type < (uint)_bufferPools.Length && _bufferPools[type] is { } found) return Unsafe.As<BufferPool<T>>(found);
        return CreateBufferPool<T>(type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private BufferPool<T> CreateBufferPool<T>(int type) where T : struct
    {
        if (type >= _bufferPools.Length) Array.Resize(ref _bufferPools, Math.Max(type + 1, Math.Max(16, _bufferPools.Length * 2)));
        var pool = new BufferPool<T>();
        _bufferPools[type] = pool;
        return pool;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddBuffer<T>(Entity entity, int capacity = 0) where T : struct
    {
        if (Execution is not null) AddBufferWithExecution<T>(entity, capacity);
        else AddBufferCore<T>(entity, capacity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddBufferWithExecution<T>(Entity entity, int capacity) where T : struct
    {
        using WorldAccess access = OpenStructure();
        AddBufferCore<T>(entity, capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddBufferCore<T>(Entity entity, int capacity) where T : struct
    {
        ComponentValues.Bind<Buffer<T>>();
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<Buffer<T>>(in record);
        int type = ComponentType<Buffer<T>>.TypeId;
        ColumnEntry[] columns = record.Chunk!.Columns;
        if ((uint)type < (uint)columns.Length && columns[type].Has(record.Row))
            throw new InvalidOperationException("The entity already has this buffer.");
        Buffer<T> data = BufferPool<T>().Rent();
        data.Reserve(capacity);
        AddKnown(entity, ref record, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyBuffer<T> ReadBuffer<T>(Entity entity) where T : struct
    {
        BeginValueRead<Buffer<T>>(false);
        bool success = false;
        try
        {
            ref readonly Buffer<T> data = ref Access<Buffer<T>>(entity, false);
            var buffer = new ReadOnlyBuffer<T>(this, in data, true);
            success = true;
            return buffer;
        }
        finally { if (!success) EndValueRead(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DynamicBuffer<T> GetBuffer<T>(Entity entity) where T : struct
    {
        BeginValueRead<Buffer<T>>(true);
        bool success = false;
        try
        {
            ref EntityRecord record = ref Resolve(entity);
            Chunk chunk = record.Chunk!;
            RequireWriteRow(chunk, record.Row);
            int type = ComponentType<Buffer<T>>.TypeId;
            ColumnEntry[] columns = chunk.Columns;
            if ((uint)type >= (uint)columns.Length || !columns[type].Has(record.Row))
                throw new ArgumentException("Entity does not have this buffer.");
            ref ColumnEntry column = ref columns[type];
            ref Buffer<T> buffer = ref ColumnEntry.At<Buffer<T>>(ref column, record.Row);
            if (buffer.Items is null) buffer = BufferPool<T>().Rent();
            if (ComponentType<Buffer<T>>.TracksWritesType)
                chunk.Changes![column.ColumnPlusOne - 1].Stamp(record.Row, NextVersion(), false, ComponentType<Buffer<T>>.TracksAddedType);
            var view = new DynamicBuffer<T>(this, ref buffer, true);
            success = true;
            return view;
        }
        finally { if (!success) EndValueRead(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlyBuffer<T> ReadRemovedBuffer<T>(Entity entity) where T : struct
    {
        BeginValueRead<Removed<Buffer<T>>>(false);
        bool success = false;
        try
        {
            ref readonly Buffer<T> data = ref Access<Removed<Buffer<T>>>(entity, false).PreviousValue;
            var buffer = new ReadOnlyBuffer<T>(this, in data, true);
            success = true;
            return buffer;
        }
        finally { if (!success) EndValueRead(); }
    }

    public void RemoveBuffer<T>(Entity entity) where T : struct => Remove<Buffer<T>>(entity);
}
