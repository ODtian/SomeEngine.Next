using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Storage;

internal abstract class PodColumnRows(int type) : CachedComponent(type)
{
    protected nint _rows;
    protected int _usedRows;
    protected int _filledType = -1;
    protected int _filledRows;
}

internal sealed unsafe partial class PodColumnBuffer(int type, int size, int initialRows) : PodColumnRows(type)
{
    internal bool IsObserved;
    private struct RowRange(Chunk chunk, int start, int capacity)
    {
        internal readonly Chunk Chunk = chunk;
        internal int Start = start, Capacity = capacity;
    }

    private readonly List<RowRange> _ranges = [];
    private byte[] _values = [];
    private GCHandle _pin;
    private int _capacityBytes;
    internal int UsedRows => _usedRows;
    internal nint Rows => _rows;

    ~PodColumnBuffer() { if (_pin.IsAllocated) _pin.Free(); }

    internal override void Reserve(int capacity) => _filledType = -1;
    internal override void Bind(Entity entity, Chunk chunk, int row) => _filledType = -1;
    internal override void BindRange(Chunk chunk, int start, int count) => _filledType = -1;
    internal override void Remove(Entity entity) => _filledType = -1;
    internal override void RemoveRange(ReadOnlySpan<Entity> entities) => _filledType = -1;
    internal override void Clear() => _filledType = -1;
    internal override void ReleaseData() => Release();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryFilledValues<T>(out Span<T> values) where T : struct
    {
        nint rows = _rows;
        if (_filledType != ComponentType<T>.TypeId) return TryChangedValues(out values);
        int count = _filledRows;
        PrefetchValues<T>(rows, count);
        values = MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>((void*)rows), count);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryBoundFilledValues<T>(out Span<T> values) where T : struct
    {
        if (_filledType < 0) return TryChangedValues(out values);
        values = AllValues<T>(_filledRows);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryChangedValues<T>(out Span<T> values) where T : struct
    {
        if (TypeId == ComponentType<T>.TypeId)
        {
            if (_filledType == -1) CheckRowCoverage();
            if (_filledType == TypeId) { values = AllValues<T>(_filledRows); return true; }
        }
        values = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckRowCoverage()
    {
        int rows = 0;
        foreach (RowRange range in _ranges)
        {
            Chunk chunk = range.Chunk;
            int count = chunk.Count;
            if (chunk.Columns[TypeId].Presence is not null || (count != 0 && range.Start != rows))
            {
                _filledType = -2;
                return;
            }
            rows += count;
        }
        _filledRows = rows;
        _filledType = TypeId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> AllValues<T>(int count) where T : struct
    {
        nint rows = _rows;
        PrefetchValues<T>(rows, count);
        return MemoryMarshal.CreateSpan(ref Unsafe.AsRef<T>((void*)rows), count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchValues<T>(nint rows, int count) where T : struct
    {
        if (Sse.IsSupported && count >= 262_144 / Unsafe.SizeOf<T>())
        {
            Sse.Prefetch0((void*)rows);
        }
    }

    internal void Release()
    {
        if (_pin.IsAllocated) _pin.Free();
        GC.SuppressFinalize(this);
        _values = [];
        _ranges.Clear();
        _rows = 0;
        _usedRows = _filledRows = _capacityBytes = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAll<T>(int count, out Span<T> values) where T : struct
    {
        if (_usedRows == count)
        {
            values = AllValues<T>(count);
            return true;
        }
        values = default;
        return false;
    }

    internal ColumnEntry Add(Chunk chunk, int capacity, int columnPlusOne)
    {
        _filledType = -1;
        int start = _usedRows;
        ReserveBytes(checked((start + capacity) * size));
        new Span<byte>((void*)(_rows + (nint)start * size), capacity * size).Clear();
        _usedRows += capacity;
        _ranges.Add(new(chunk, start, capacity));
        return new() { Values = _values, Rows = _rows + (nint)start * size, ColumnPlusOne = columnPlusOne };
    }

    internal void Grow(Chunk chunk, int capacity)
    {
        _filledType = -1;
        int index = _ranges.Count - 1;
        while (!ReferenceEquals(_ranges[index].Chunk, chunk)) index--;
        RowRange range = _ranges[index];
        int delta = capacity - range.Capacity;
        int tailStart = range.Start + range.Capacity;
        int tailRows = _usedRows - tailStart;
        ReserveBytes(checked((_usedRows + delta) * size));
        new Span<byte>((void*)(_rows + (nint)tailStart * size), tailRows * size)
            .CopyTo(new Span<byte>((void*)(_rows + (nint)(tailStart + delta) * size), tailRows * size));
        new Span<byte>((void*)(_rows + (nint)tailStart * size), delta * size).Clear();
        range.Capacity = capacity;
        _ranges[index] = range;
        _usedRows += delta;
        ref ColumnEntry column = ref chunk.Columns[TypeId];
        column.Values = _values;
        column.Rows = _rows + (nint)range.Start * size;
        for (int i = index + 1; i < _ranges.Count; i++)
        {
            RowRange moved = _ranges[i];
            moved.Start += delta;
            _ranges[i] = moved;
            ref ColumnEntry movedColumn = ref moved.Chunk.Columns[TypeId];
            movedColumn.Values = _values;
            movedColumn.Rows = _rows + (nint)moved.Start * size;
        }
        for (int i = index + 1; i < _ranges.Count; i++)
        {
            Chunk moved = _ranges[i].Chunk;
            if (moved.Archetype.Cached is { } cached)
                foreach (CachedComponent data in cached)
                    if (data.TypeId == TypeId && !ReferenceEquals(data, this)) data.BindRange(moved, 0, moved.Count);
        }
    }

    private void ReserveBytes(int needed)
    {
        if (needed <= _capacityBytes) return;
        int required = Math.Max(4096, Math.Max(needed, checked(initialRows * size)));
        int capacity = _capacityBytes == 0 ? required
            : checked((Math.Max(required, _capacityBytes + _capacityBytes / 2) + 4095) & ~4095);
        bool pageAligned = capacity >= 32_768;
        int prefix = pageAligned ? (TypeId & 3) * 1_024 : 0;
        bool firstSmall = _capacityBytes == 0 && capacity <= 262_144;
        int bankOffset = firstSmall
            ? ((TypeId & 1) * 32 ^ ((TypeId >> 1) & 1) * 48)
            : (TypeId & 1) * 32 + ((TypeId >> 1) & 1) * 16;
        nuint alignment = pageAligned ? 4_096u : 64u;
        int length = checked(capacity + Math.Max(63, (int)alignment - 1) + prefix + bankOffset);
        bool fixedArray = _capacityBytes == 0 || length < 85_000;
        byte[] values = GC.AllocateUninitializedArray<byte>(length, pinned: fixedArray);
        GCHandle pin = fixedArray ? default : GCHandle.Alloc(values, GCHandleType.Pinned);
        nuint address = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(values));
        nint rows = (nint)(((address + alignment - 1) & ~(alignment - 1)) + (nuint)(prefix + bankOffset));
        new ReadOnlySpan<byte>((void*)_rows, _usedRows * size).CopyTo(new Span<byte>((void*)rows, _usedRows * size));
        GCHandle previousPin = _pin;
        _values = values;
        _pin = pin;
        _rows = rows;
        _capacityBytes = capacity;
        foreach (RowRange range in _ranges)
        {
            Chunk chunk = range.Chunk;
            ref ColumnEntry column = ref chunk.Columns[TypeId];
            column.Values = values;
            column.Rows = rows + (nint)range.Start * size;
        }
        if (previousPin.IsAllocated) previousPin.Free();
    }
}
