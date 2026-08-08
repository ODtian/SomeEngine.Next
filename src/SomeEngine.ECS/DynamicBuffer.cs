using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public ref struct DynamicBuffer<T> where T : struct, IBufferElement
{
    private readonly Owners.Buffers _buffers;
    private readonly Chunk _chunk;
    private readonly int _row;
    private readonly int _headerColumn;
    private readonly int _inlineColumn;
    private readonly uint _writeVersion;

    internal DynamicBuffer(
        Owners.Buffers buffers,
        Chunk chunk,
        int row,
        int headerColumn,
        int inlineColumn,
        uint writeVersion)
    {
        _buffers = buffers;
        _chunk = chunk;
        _row = row;
        _headerColumn = headerColumn;
        _inlineColumn = inlineColumn;
        _writeVersion = writeVersion;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ReadOnlyHeader.Count;
    }

    public int Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly var header = ref ReadOnlyHeader;
            return header.HasOverflow ? header.OverflowCapacity : header.InlineCapacity;
        }
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly var readOnlyHeader = ref ReadOnlyHeader;
            ThrowRange(index, readOnlyHeader.Count);

            if (readOnlyHeader.HasOverflow)
            {
                ref var header = ref WritableOverflowHeader;
                MarkHeaderChanged();
                return ref header.OverflowWriteSpan[index];
            }

            ref var inline = ref Inline;
            MarkChanged();
            return ref inline[index];
        }
    }

    public T Read(int index)
    {
        ref readonly var header = ref ReadOnlyHeader;
        ThrowRange(index, header.Count);

        if (header.HasOverflow)
            return header.OverflowReadSpan[index];

        return ReadOnlyInline[index];
    }

    public void Add(in T element)
    {
        ref var header = ref Header;
        int index = header.Count;
        if (header.HasOverflow || index >= header.InlineCapacity)
        {
            var overflow = EnsureOverflow(ref header, index + 1);
            overflow[index] = element;
            header.Count = index + 1;
            MarkHeaderChanged();
            return;
        }

        Inline[index] = element;
        header.Count = index + 1;
        MarkChanged();
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity < 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        ref readonly var readOnlyHeader = ref ReadOnlyHeader;
        int currentCapacity = readOnlyHeader.HasOverflow
            ? readOnlyHeader.OverflowCapacity
            : readOnlyHeader.InlineCapacity;
        if (capacity <= currentCapacity)
            return;

        ref var header = ref Header;
        EnsureOverflow(ref header, capacity);
        MarkHeaderChanged();
    }

    public void SwapRemoveAt(int index)
    {
        ref readonly var readOnlyHeader = ref ReadOnlyHeader;
        ThrowRange(index, readOnlyHeader.Count);

        ref DynamicBufferHeader<T> header = ref Header;
        if (header.HasOverflow)
            header = ref WritableOverflowHeader;

        int last = header.Count - 1;
        if (header.HasOverflow)
        {
            Span<T> overflow = header.OverflowWriteSpan;
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
            inline[index] = inline[last];
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            inline[last] = default;
        header.Count = last;
        MarkChanged();
    }

    public void Clear()
    {
        Clear(markAdded: false, force: false);
    }

    private void Clear(bool markAdded, bool force)
    {
        if (!force)
        {
            ref readonly var readOnlyHeader = ref ReadOnlyHeader;
            if (readOnlyHeader.Count == 0 && !readOnlyHeader.HasOverflow)
                return;
        }

        ref var header = ref Header;
        int count = header.Count;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        if (header.HasOverflow)
        {
            if (containsReferences && _chunk.OwnsBufferOverflow(in header))
            {
                header.OverflowWriteSpan[..count].Clear();
            }

            _chunk.SetOwnedBufferOverflow(ref header, null);
            if (containsReferences)
                ClearInactiveInlineReferences(header.InlineCapacity);
        }
        else if (containsReferences)
        {
            InlineSpan(count).Clear();
        }

        header.Count = 0;
        MarkChanged(markAdded);
    }

    internal void ReplaceWith(scoped ReadOnlySpan<T> values)
    {
        values.CopyTo(ReplaceWithUninitialized(values.Length));
    }

    internal void InitializeWith(scoped ReadOnlySpan<T> values)
    {
        values.CopyTo(InitializeUninitialized(values.Length));
    }

    internal Span<T> ReplaceWithUninitialized(int count)
    {
        return PrepareUninitialized(count, markAdded: false, markVersions: true);
    }

    internal Span<T> InitializeUninitialized(int count)
    {
        return PrepareUninitialized(count, markAdded: true, markVersions: true);
    }

    internal Span<T> LoadUninitialized(int count)
    {
        return PrepareUninitialized(count, markAdded: false, markVersions: false);
    }

    private Span<T> PrepareUninitialized(
        int count,
        bool markAdded,
        bool markVersions)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        if (count == 0)
            return PrepareEmpty(markAdded, markVersions);

        ref var header = ref Header;
        int oldCount = header.Count;
        bool oldUsedOverflow = header.HasOverflow;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        if (count <= header.InlineCapacity)
            return PrepareInline(
                ref header,
                count,
                oldCount,
                oldUsedOverflow,
                containsReferences,
                markAdded,
                markVersions);

        return PrepareOverflow(
            ref header,
            count,
            oldCount,
            oldUsedOverflow,
            containsReferences,
            markAdded,
            markVersions);
    }

    private Span<T> PrepareEmpty(bool markAdded, bool markVersions)
    {
        if (markVersions)
            Clear(markAdded, force: markAdded);
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
        bool markAdded,
        bool markVersions)
    {
        ClearOldOverflow(ref header, oldCount, oldUsedOverflow, containsReferences);

        var inline = InlineSpan(header.InlineCapacity);
        if (containsReferences)
            inline.Clear();

        header.Count = count;
        if (markVersions)
            MarkChanged(markAdded);
        return inline[..count];
    }

    private Span<T> PrepareOverflow(
        ref DynamicBufferHeader<T> header,
        int count,
        int oldCount,
        bool oldUsedOverflow,
        bool containsReferences,
        bool markAdded,
        bool markVersions)
    {
        Span<T> overflow =
            EnsureReplacementOverflow(ref header, count, oldCount, containsReferences);

        if (containsReferences && !oldUsedOverflow)
            InlineSpan(header.InlineCapacity).Clear();

        header.Count = count;
        if (markVersions)
            MarkHeaderChanged(markAdded);
        return overflow[..count];
    }

    private void ClearOldOverflow(
        ref DynamicBufferHeader<T> header,
        int oldCount,
        bool oldUsedOverflow,
        bool containsReferences)
    {
        if (!oldUsedOverflow)
            return;

        if (containsReferences && _chunk.OwnsBufferOverflow(in header))
            header.OverflowWriteSpan[..oldCount].Clear();

        _chunk.SetOwnedBufferOverflow(ref header, null);
    }

    private Span<T> EnsureReplacementOverflow(
        ref DynamicBufferHeader<T> header,
        int count,
        int oldCount,
        bool containsReferences)
    {
        ReadOnlySpan<T> overflow = header.OverflowReadSpan;
        if (!header.HasOverflow || overflow.Length < count)
        {
            int oldCapacity = header.HasOverflow ? overflow.Length : header.InlineCapacity;
            var replacement = new T[Math.Max(count, Math.Max(1, oldCapacity * 2))];
            _chunk.RecordInheritedBufferOverflowReplacement(in header);
            _chunk.SetOwnedBufferOverflow(ref header, replacement);
            return replacement;
        }

        if (!_chunk.OwnsBufferOverflow(in header))
        {
            // This API returns uninitialized storage. Retain capacity without copying values the
            // caller is about to overwrite.
            var replacement = new T[overflow.Length];
            _chunk.RecordInheritedBufferOverflowReplacement(in header);
            _chunk.SetOwnedBufferOverflow(ref header, replacement);
            return replacement;
        }

        if (containsReferences && oldCount > count)
            header.OverflowWriteSpan.Slice(count, oldCount - count).Clear();

        return header.OverflowWriteSpan;
    }

    private void ClearWithoutLog()
    {
        ref readonly var readOnlyHeader = ref ReadOnlyHeader;
        if (readOnlyHeader.Count == 0 && !readOnlyHeader.HasOverflow)
            return;

        ref var header = ref Header;
        int count = header.Count;
        bool containsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        if (header.HasOverflow)
        {
            if (containsReferences && _chunk.OwnsBufferOverflow(in header))
            {
                header.OverflowWriteSpan[..count].Clear();
            }

            _chunk.SetOwnedBufferOverflow(ref header, null);
            if (containsReferences)
                ClearInactiveInlineReferences(header.InlineCapacity);
        }
        else if (containsReferences)
        {
            InlineSpan(count).Clear();
        }

        header.Count = 0;
    }

    public Span<T> AsSpan()
    {
        ref readonly var readOnlyHeader = ref ReadOnlyHeader;
        if (readOnlyHeader.HasOverflow)
        {
            ref var header = ref WritableOverflowHeader;
            MarkHeaderChanged();
            return header.OverflowWriteSpan[..header.Count];
        }

        ref var writableHeader = ref Header;
        MarkChanged();
        return InlineSpan(writableHeader.Count);
    }

    public ReadOnlySpan<T> ReadSpan()
    {
        ref readonly var header = ref ReadOnlyHeader;

        if (header.HasOverflow)
            return header.OverflowReadSpan[..header.Count];

        return InlineReadOnlySpan(header.Count);
    }

    private ref readonly DynamicBufferHeader<T> ReadOnlyHeader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentReadOnlyRef<DynamicBufferHeader<T>>(_headerColumn, _row);
    }

    private ref DynamicBufferHeader<T> Header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentRef<DynamicBufferHeader<T>>(_headerColumn, _row);
    }

    private ref DynamicBufferHeader<T> WritableOverflowHeader
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetBufferHeaderWithWritableOverflow<T>(_headerColumn, _row);
    }

    private ref DynamicBufferInline<T> Inline
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentRef<DynamicBufferInline<T>>(_inlineColumn, _row);
    }

    private ref readonly DynamicBufferInline<T> ReadOnlyInline
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentReadOnlyRef<DynamicBufferInline<T>>(_inlineColumn, _row);
    }

    private Span<T> InlineSpan(int count)
    {
        if (count == 0)
            return Span<T>.Empty;

        ref var inline = ref Inline;
        return MemoryMarshal.CreateSpan(ref inline[0], count);
    }

    private ReadOnlySpan<T> InlineReadOnlySpan(int count)
    {
        if (count == 0)
            return ReadOnlySpan<T>.Empty;

        ref readonly var inline = ref ReadOnlyInline;
        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.AsRef(in inline[0]),
            count);
    }

    private Span<T> EnsureOverflow(ref DynamicBufferHeader<T> header, int minCapacity)
    {
        if (header.HasOverflow)
        {
            ReadOnlySpan<T> existing = header.OverflowReadSpan;
            if (existing.Length >= minCapacity)
            {
                Span<T> writable = _chunk.OwnsBufferOverflow(in header)
                    ? header.OverflowWriteSpan
                    : _chunk.EnsureOwnedBufferOverflow(ref header);
                ClearInactiveInlineReferences(header.InlineCapacity);
                return writable;
            }

            int newCapacity = Math.Max(minCapacity, existing.Length * 2);
            var grown = new T[newCapacity];
            existing[..header.Count].CopyTo(grown);
            _chunk.RecordInheritedBufferOverflowReplacement(in header);
            _chunk.SetOwnedBufferOverflow(ref header, grown);
            ClearInactiveInlineReferences(header.InlineCapacity);
            return grown;
        }

        int capacity = Math.Max(minCapacity, Math.Max(1, header.InlineCapacity * 2));
        var created = new T[capacity];
        int inlineCount = Math.Min(header.Count, header.InlineCapacity);
        ref var inline = ref Inline;
        for (int i = 0; i < inlineCount; i++)
            created[i] = inline[i];

        _chunk.SetOwnedBufferOverflow(ref header, created);
        ClearInactiveInlineReferences(header.InlineCapacity);
        return created;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearInactiveInlineReferences(int inlineCapacity)
    {
        if (inlineCapacity == 0 || !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            return;

        InlineSpan(inlineCapacity).Clear();
    }

    private void MarkChanged()
    {
        MarkChanged(markAdded: false);
    }

    private void MarkChanged(bool markAdded)
    {
        MarkHeaderChanged(markAdded);
        if (!markAdded)
            _buffers.MarkWrite(_chunk, _inlineColumn, _row, _writeVersion);
    }

    private void MarkHeaderChanged()
    {
        MarkHeaderChanged(markAdded: false);
    }

    private void MarkHeaderChanged(bool markAdded)
    {
        if (markAdded)
        {
            _buffers.MarkAdd(_chunk, _headerColumn, _row, _writeVersion);
            _buffers.MarkAdd(_chunk, _inlineColumn, _row, _writeVersion);
        }
        else
        {
            _buffers.MarkWrite(_chunk, _headerColumn, _row, _writeVersion);
        }
    }

    private static void ThrowRange(int index, int count)
    {
        if ((uint)index >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}

