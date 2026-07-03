using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public ref struct BufferView<T>
    where T : struct, IBufferElement
{
    private readonly Chunk _chunk;
    private readonly int _row;
    private readonly int _headerColumn;
    private readonly int _inlineColumn;

    internal BufferView(
        Chunk chunk,
        int row,
        int headerColumn,
        int inlineColumn)
    {
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
            var header = Header;
            return header.Overflow?.Length ?? header.InlineCapacity;
        }
    }

    public T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Read(index);
    }

    public T Read(int index)
    {
        var header = Header;
        ThrowRange(index, header.Count);

        if (header.Overflow is not null)
            return header.Overflow[index];

        return Inline.Elements[index];
    }

    public ReadOnlySpan<T> AsSpan()
    {
        var header = Header;

        if (header.Overflow is not null)
            return header.Overflow.AsSpan(0, header.Count);

        return InlineSpan(header.Count);
    }

    private DynamicBufferHeader<T> Header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunk.ReadComponent<DynamicBufferHeader<T>>(_headerColumn, _row);
    }

    private ref DynamicBufferInline<T> Inline
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunk.GetComponentRef<DynamicBufferInline<T>>(_inlineColumn, _row);
    }

    private ReadOnlySpan<T> InlineSpan(int count)
    {
        if (count == 0)
            return ReadOnlySpan<T>.Empty;

        ref var inline = ref Inline;
        return MemoryMarshal.CreateReadOnlySpan(ref inline.Elements[0], count);
    }

    private static void ThrowRange(int index, int count)
    {
        if ((uint)index >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }
}

