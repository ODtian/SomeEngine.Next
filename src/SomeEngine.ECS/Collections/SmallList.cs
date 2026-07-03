using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Collections;

[InlineArray(3)]
internal struct SmallInlineStorage<T>
{
    private T _element0;
}

/// <summary>
/// 小集合容器：前 3 个元素走 inline 路径，第 4 个元素起切到 overflow 数组。
/// </summary>
public struct SmallList<T>
{
    private const int InlineCapacity = 3;

    private SmallInlineStorage<T> _inline;
    private T[]? _overflow;
    private int _count;

    public readonly int Count => _count;

    [UnscopedRef]
    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));

            if (_overflow is not null)
                return ref _overflow[index];

            return ref _inline[index];
        }
    }

    public void Add(T item)
    {
        if (_overflow is null && _count < InlineCapacity)
        {
            _inline[_count++] = item;
            return;
        }

        EnsureOverflowCapacity(_count + 1);
        _overflow![_count++] = item;
    }

    public void EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        if (capacity <= InlineCapacity)
            return;

        EnsureOverflowCapacity(capacity);
    }

    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_overflow is null && _count < InlineCapacity)
        {
            var span = MemoryMarshal.CreateSpan(ref _inline[0], InlineCapacity);
            if (index < _count)
                span[index.._count].CopyTo(span[(index + 1)..]);

            span[index] = item;
            _count++;
            return;
        }

        EnsureOverflowCapacity(_count + 1);
        if (index < _count)
            Array.Copy(_overflow!, index, _overflow!, index + 1, _count - index);

        _overflow![index] = item;
        _count++;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));

        int lastIndex = _count - 1;

        if (_overflow is not null)
        {
            if (index < lastIndex)
                Array.Copy(_overflow, index + 1, _overflow, index, lastIndex - index);

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                _overflow[lastIndex] = default!;
        }
        else
        {
            var span = MemoryMarshal.CreateSpan(ref _inline[0], InlineCapacity);
            if (index < lastIndex)
                span[(index + 1).._count].CopyTo(span[index..]);

            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                span[lastIndex] = default!;
        }

        _count--;
    }

    public void Clear()
    {
        if (_count == 0)
            return;

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            AsSpan().Clear();

        _count = 0;
    }

    public Span<T> AsSpan()
    {
        if (_overflow is not null)
            return _overflow.AsSpan(0, _count);

        return MemoryMarshal.CreateSpan(ref _inline[0], InlineCapacity).Slice(0, _count);
    }

    public readonly ReadOnlySpan<T> ReadSpan()
    {
        if (_overflow is not null)
            return _overflow.AsSpan(0, _count);

        return MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.AsRef(in _inline[0]),
            InlineCapacity).Slice(0, _count);
    }

    public readonly Enumerator GetEnumerator() => new(this);

    private void EnsureOverflowCapacity(int requiredCapacity)
    {
        bool wasNull = _overflow is null;
        ArrayGrowthExtensions.EnsureCapacity(ref _overflow, requiredCapacity, InlineCapacity + 1);
        if (wasNull)
            AsInlineSpan().CopyTo(_overflow);
    }

    private Span<T> AsInlineSpan() =>
        MemoryMarshal.CreateSpan(ref _inline[0], Math.Min(_count, InlineCapacity));

    public struct Enumerator
    {
        private readonly SmallList<T> _list;
        private int _index;

        internal Enumerator(SmallList<T> list)
        {
            _list = list;
            _index = -1;
        }

        public readonly T Current => _list[_index];

        public bool MoveNext()
        {
            int nextIndex = _index + 1;
            if (nextIndex >= _list.Count)
                return false;

            _index = nextIndex;
            return true;
        }
    }
}

