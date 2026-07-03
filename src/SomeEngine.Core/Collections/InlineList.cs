using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Core.Collections;

public struct InlineList<T, TInlineStorage> : IInlineList<T>
    where TInlineStorage : struct
{
    private TInlineStorage _inline;
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

            return ref InlineSpan()[index];
        }
    }

    public void Add(T item)
    {
        int inlineCapacity = InlineCapacity;
        if (_overflow is null && _count < inlineCapacity)
        {
            InlineSpan()[_count++] = item;
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

        int inlineCapacity = InlineCapacity;
        if (_overflow is null && _count < inlineCapacity)
        {
            Span<T> span = InlineSpan();
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

            ClearSlot(_overflow.AsSpan(lastIndex, 1));
        }
        else
        {
            Span<T> span = InlineSpan();
            if (index < lastIndex)
                span[(index + 1).._count].CopyTo(span[index..]);

            ClearSlot(span.Slice(lastIndex, 1));
        }

        _count--;
    }

    public int IndexOf(T item)
    {
        var span = AsSpan();
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < span.Length; i++)
        {
            if (comparer.Equals(span[i], item))
                return i;
        }

        return -1;
    }

    public bool RemoveStable(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
            return false;

        RemoveAt(index);
        return true;
    }

    public bool RemoveSwapBack(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
            return false;

        RemoveSwapAt(index);
        return true;
    }

    public void RemoveSwapAt(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));

        int lastIndex = _count - 1;
        if (index != lastIndex)
            this[index] = this[lastIndex];

        RemoveAt(lastIndex);
    }

    public void Clear()
    {
        if (_count == 0)
            return;

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            AsSpan().Clear();

        _count = 0;
    }

    [UnscopedRef]
    public Span<T> AsSpan()
    {
        if (_overflow is not null)
            return _overflow.AsSpan(0, _count);

        return InlineSpan()[.._count];
    }

    [UnscopedRef]
    public InlineListEnumerator<T> GetEnumerator() => new(AsSpan());

    private static int InlineCapacity
    {
        get
        {
            try
            {
                return Layout.InlineCapacity;
            }
            catch (TypeInitializationException ex)
                when (ex.InnerException is InvalidOperationException invalid)
            {
                ExceptionDispatchInfo.Capture(invalid).Throw();
                throw;
            }
        }
    }

    private void EnsureOverflowCapacity(int requiredCapacity)
    {
        bool wasNull = _overflow is null;
        int inlineCapacity = InlineCapacity;
        EnsureArrayCapacity(ref _overflow, requiredCapacity, inlineCapacity + 1);
        if (wasNull && _count > 0)
        {
            Span<T> inlineItems = InlineSpan()[..global::System.Math.Min(_count, inlineCapacity)];
            inlineItems.CopyTo(_overflow);
            ClearSlot(inlineItems);
        }
    }

    [UnscopedRef]
    private Span<T> InlineSpan()
    {
        int inlineCapacity = InlineCapacity;
        ref T first = ref Unsafe.As<TInlineStorage, T>(ref _inline);
        return MemoryMarshal.CreateSpan(ref first, inlineCapacity);
    }

    private static void ClearSlot(Span<T> slot)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            slot.Clear();
    }

    private static void EnsureArrayCapacity(ref T[]? array, int required, int minimumCapacity)
    {
        if (array is not null && array.Length >= required)
            return;

        int next = array is { Length: > 0 } ? array.Length * 2 : minimumCapacity;
        if (next < required)
            next = required;

        if (next < minimumCapacity)
            next = minimumCapacity;

        Array.Resize(ref array, next);
    }

    private static class Layout
    {
        private static readonly int ValidatedInlineCapacity = Validate();

        public static int InlineCapacity => ValidatedInlineCapacity;

        private static int Validate()
        {
            int elementSize = Unsafe.SizeOf<T>();
            int storageSize = Unsafe.SizeOf<TInlineStorage>();
            int capacity = storageSize / elementSize;
            if (capacity <= 0)
                throw CreateCapacityException(storageSize);

            if (storageSize % elementSize != 0)
                throw new InvalidOperationException(
                    $"Inline list storage '{typeof(TInlineStorage).FullName}' must declare exactly one instance field compatible with element type '{typeof(T).FullName}'.");

            return capacity;
        }

        private static InvalidOperationException CreateCapacityException(int storageSize)
        {
            if (storageSize <= 1)
            {
                return new InvalidOperationException(
                    $"Inline list storage '{typeof(TInlineStorage).FullName}' must use InlineArrayAttribute.");
            }

            return new InvalidOperationException(
                $"Inline list storage '{typeof(TInlineStorage).FullName}' must declare exactly one instance field compatible with element type '{typeof(T).FullName}'.");
        }
    }

}


