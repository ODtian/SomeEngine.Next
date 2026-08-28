namespace SomeEngine.Job;

/// <summary>
/// Fixed-capacity owner-bottom/thief-top Chase-Lev deque. Only the owning worker may call
/// <see cref="TryPush"/> and <see cref="TryPop"/>; any thread may call <see cref="TrySteal"/>.
/// </summary>
internal sealed class ChaseLevDeque<T>
    where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _top;
    private long _bottom;

    internal ChaseLevDeque(int minimumCapacity)
    {
        int capacity = RoundUpPowerOfTwo(Math.Max(2, minimumCapacity));
        _items = new T[capacity];
        _mask = capacity - 1;
    }

    internal bool IsEmpty =>
        Volatile.Read(ref _top) >= Volatile.Read(ref _bottom);

    internal bool TryPush(in T item)
    {
        long bottom = Volatile.Read(ref _bottom);
        long top = Volatile.Read(ref _top);
        if (bottom - top >= _items.Length)
            return false;

        _items[(int)bottom & _mask] = item;
        Volatile.Write(ref _bottom, bottom + 1);
        return true;
    }

    internal bool TryPop(out T item)
    {
        long bottom = Volatile.Read(ref _bottom) - 1;
        Volatile.Write(ref _bottom, bottom);
        Thread.MemoryBarrier();
        long top = Volatile.Read(ref _top);
        if (top <= bottom)
        {
            item = _items[(int)bottom & _mask];
            if (top == bottom)
            {
                if (Interlocked.CompareExchange(ref _top, top + 1, top) != top)
                {
                    item = default;
                    Volatile.Write(ref _bottom, top + 1);
                    return false;
                }

                Volatile.Write(ref _bottom, top + 1);
            }

            return true;
        }

        Volatile.Write(ref _bottom, bottom + 1);
        item = default;
        return false;
    }

    internal bool TrySteal(out T item)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long top = Volatile.Read(ref _top);
            Thread.MemoryBarrier();
            long bottom = Volatile.Read(ref _bottom);
            if (top >= bottom)
                break;

            T candidate = _items[(int)top & _mask];
            if (Interlocked.CompareExchange(ref _top, top + 1, top) == top)
            {
                item = candidate;
                return true;
            }
        }

        item = default;
        return false;
    }

    private static int RoundUpPowerOfTwo(int value)
    {
        if (value > 1 << 30)
            throw new ArgumentOutOfRangeException(nameof(value));
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
