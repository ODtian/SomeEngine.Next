using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS;

public ref struct DynamicBuffer<T> where T : struct, IBufferElement
{
    private readonly Owners.Buffers _buffers;
    private readonly Chunk _chunk;
    private readonly int _row;
    private readonly int _headerColumn;
    private readonly int _inlineColumn;

    internal DynamicBuffer(
        Owners.Buffers buffers,
        Chunk chunk,
        int row,
        int headerColumn,
        int inlineColumn)
    {
        _buffers = buffers;
        _chunk = chunk;
        _row = row;
        _headerColumn = headerColumn;
        _inlineColumn = inlineColumn;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Header.Count;
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref var header = ref Header;
            return header.Overflow?.Length ?? header.InlineCapacity;
        }
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref var header = ref Header;
            ThrowRange(index, header.Count);

            if (header.Overflow is not null)
            {
                MarkHeaderChanged();
                return ref header.Overflow[index];
            }

            MarkChanged();
            return ref Inline.Elements[index];
        }
    }

    public T Read(int index)
    {
        ref var header = ref Header;
        ThrowRange(index, header.Count);

        if (header.Overflow is not null)
            return header.Overflow[index];

        return Inline.Elements[index];
    }

    public void Add(in T element)
    {
        ref var header = ref Header;
        int index = header.Count;
        if (header.Overflow is not null || index >= header.InlineCapacity)
        {
            var overflow = EnsureOverflow(ref header, index + 1);
            overflow[index] = element;
            header.Count = index + 1;
            MarkHeaderChanged();
            return;
        }

        Inline.Elements[index] = element;
        header.Count = index + 1;
        MarkChanged();
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        ref var header = ref Header;
        if (capacity <= Capacity)
            return;

        EnsureOverflow(ref header, capacity);
        MarkHeaderChanged();
    }

    public void SwapRemoveAt(int index)
    {
        ref var header = ref Header;
        ThrowRange(index, header.Count);

        int last = header.Count - 1;
        if (header.Overflow is not null)
        {
            var overflow = header.Overflow;
            if (index != last)
                overflow[index] = overflow[last];
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                overflow[last] = default;
            header.Count = last;
            MarkHeaderChanged();
            return;
        }

        ref var inline = ref Inline;
        if (index != last)
            inline.Elements[index] = inline.Elements[last];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            inline.Elements[last] = default;
        header.Count = last;
        MarkChanged();
    }

    public void Clear()
    {
        Clear(SerializationChangeKind.BufferChanged, force: false);
    }

    private void Clear(SerializationChangeKind kind, bool force)
    {
        ref var header = ref Header;
        int count = header.Count;
        if (!force && count == 0 && header.Overflow is null)
            return;

        if (header.Overflow is not null)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                header.Overflow.AsSpan(0, count).Clear();
            header.Overflow = null;
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            InlineSpan(count).Clear();
        }

        header.Count = 0;
        MarkChanged(kind);
    }

    internal void ReplaceWith(scoped ReadOnlySpan<T> values)
    {
        values.CopyTo(ReplaceWithUninitialized(values.Length));
    }

    internal void ReplaceWith(scoped ReadOnlySpan<T> values, SerializationChangeKind kind)
    {
        values.CopyTo(ReplaceWithUninitialized(values.Length, kind));
    }

    internal Span<T> ReplaceWithUninitialized(int count)
    {
        return ReplaceWithUninitialized(count, SerializationChangeKind.BufferChanged);
    }

    internal Span<T> ReplaceWithUninitialized(int count, SerializationChangeKind kind)
    {
        return PrepareUninitialized(count, kind, recordChange: true);
    }

    internal Span<T> LoadUninitialized(int count)
    {
        return PrepareUninitialized(count, SerializationChangeKind.BufferChanged, recordChange: false);
    }

    private Span<T> PrepareUninitialized(
        int count,
        SerializationChangeKind kind,
        bool recordChange)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        ref var header = ref Header;
        if (count == 0)
            return PrepareEmpty(kind, recordChange);

        int oldCount = header.Count;
        bool oldUsedOverflow = header.Overflow is not null;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        if (count <= header.InlineCapacity)
            return PrepareInline(
                ref header,
                count,
                oldCount,
                oldUsedOverflow,
                containsReferences,
                kind,
                recordChange);

        return PrepareOverflow(
            ref header,
            count,
            oldCount,
            oldUsedOverflow,
            containsReferences,
            kind,
            recordChange);
    }

    private Span<T> PrepareEmpty(SerializationChangeKind kind, bool recordChange)
    {
        if (recordChange)
            Clear(kind, force: kind == SerializationChangeKind.BufferAdded);
        else
            ClearWithoutLog();

        return Span<T>.Empty;
    }

    private Span<T> PrepareInline(
        ref DynamicBufferHeader<T> header,
        int count,
        int oldCount,
        bool oldUsedOverflow,
        bool containsReferences,
        SerializationChangeKind kind,
        bool recordChange)
    {
        ClearOldOverflow(ref header, oldCount, oldUsedOverflow, containsReferences);

        var inline = InlineSpan(header.InlineCapacity);
        if (containsReferences)
            inline.Clear();

        header.Count = count;
        if (recordChange)
            MarkChanged(kind);
        return inline[..count];
    }

    private Span<T> PrepareOverflow(
        ref DynamicBufferHeader<T> header,
        int count,
        int oldCount,
        bool oldUsedOverflow,
        bool containsReferences,
        SerializationChangeKind kind,
        bool recordChange)
    {
        var overflow = EnsureReplacementOverflow(ref header, count, oldCount, containsReferences);

        if (containsReferences && !oldUsedOverflow)
            InlineSpan(header.InlineCapacity).Clear();

        header.Overflow = overflow;
        header.Count = count;
        if (recordChange)
            MarkHeaderChanged(kind);
        return overflow.AsSpan(0, count);
    }

    private void ClearOldOverflow(
        ref DynamicBufferHeader<T> header,
        int oldCount,
        bool oldUsedOverflow,
        bool containsReferences)
    {
        if (!oldUsedOverflow)
            return;

        if (containsReferences)
            header.Overflow!.AsSpan(0, oldCount).Clear();
        header.Overflow = null;
    }

    private static T[] EnsureReplacementOverflow(
        ref DynamicBufferHeader<T> header,
        int count,
        int oldCount,
        bool containsReferences)
    {
        var overflow = header.Overflow;
        if (overflow is null || overflow.Length < count)
        {
            int oldCapacity = overflow?.Length ?? header.InlineCapacity;
            return new T[Math.Max(count, Math.Max(1, oldCapacity * 2))];
        }

        if (containsReferences && oldCount > count)
            overflow.AsSpan(count, oldCount - count).Clear();

        return overflow;
    }

    private void ClearWithoutLog()
    {
        ref var header = ref Header;
        int count = header.Count;
        if (count == 0 && header.Overflow is null)
            return;

        if (header.Overflow is not null)
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                header.Overflow.AsSpan(0, count).Clear();
            header.Overflow = null;
        }
        else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            InlineSpan(count).Clear();
        }

        header.Count = 0;
    }

    public Span<T> AsSpan()
    {
        ref var header = ref Header;
        if (header.Overflow is not null)
        {
            MarkHeaderChanged();
            return header.Overflow.AsSpan(0, header.Count);
        }

        MarkChanged();
        return InlineSpan(header.Count);
    }

    public ReadOnlySpan<T> ReadSpan()
    {
        ref var header = ref Header;

        if (header.Overflow is not null)
            return header.Overflow.AsSpan(0, header.Count);

        return InlineSpan(header.Count);
    }

    private ref DynamicBufferHeader<T> Header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentRef<DynamicBufferHeader<T>>(_headerColumn, _row);
    }

    private ref DynamicBufferInline<T> Inline
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentRef<DynamicBufferInline<T>>(_inlineColumn, _row);
    }

    private Span<T> InlineSpan(int count)
    {
        if (count == 0)
            return Span<T>.Empty;

        ref var inline = ref Inline;
        return MemoryMarshal.CreateSpan(ref inline.Elements[0], count);
    }

    private T[] EnsureOverflow(ref DynamicBufferHeader<T> header, int minCapacity)
    {
        if (header.Overflow is { } existing)
        {
            if (existing.Length >= minCapacity)
                return existing;

            int newCapacity = Math.Max(minCapacity, existing.Length * 2);
            var grown = new T[newCapacity];
            existing.AsSpan(0, header.Count).CopyTo(grown);
            header.Overflow = grown;
            return grown;
        }

        int capacity = Math.Max(minCapacity, Math.Max(1, header.InlineCapacity * 2));
        var created = new T[capacity];
        int inlineCount = Math.Min(header.Count, header.InlineCapacity);
        ref var inline = ref Inline;
        for (int i = 0; i < inlineCount; i++)
            created[i] = inline.Elements[i];

        header.Overflow = created;
        return created;
    }

    private void MarkChanged()
    {
        MarkChanged(SerializationChangeKind.BufferChanged);
    }

    private void MarkChanged(SerializationChangeKind kind)
    {
        MarkHeaderChanged(kind);
        if (kind != SerializationChangeKind.BufferAdded)
            _buffers.MarkChunk(_chunk, _inlineColumn);
    }

    private void MarkHeaderChanged()
    {
        MarkHeaderChanged(SerializationChangeKind.BufferChanged);
    }

    private void MarkHeaderChanged(SerializationChangeKind kind)
    {
        if (kind == SerializationChangeKind.BufferAdded)
        {
            _buffers.MarkAdd(_chunk, _headerColumn, _row);
            _buffers.MarkAdd(_chunk, _inlineColumn, _row);
        }
        else
        {
            _buffers.MarkChunk(_chunk, _headerColumn);
        }

        _buffers.Write(
            kind,
            _chunk.Entities[_row],
            BufferComponents.Header<T>());
    }

    private static void ThrowRange(int index, int count)
    {
        if ((uint)index >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}

