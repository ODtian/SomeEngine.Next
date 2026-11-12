namespace SomeEngine.ECS.Storage;

internal struct ComponentBits
{
    private ulong _a, _b, _c, _d;
    private ulong[]? _tail;

    internal readonly int First
    {
        get
        {
            if (_a != 0) return System.Numerics.BitOperations.TrailingZeroCount(_a);
            if (_b != 0) return 64 + System.Numerics.BitOperations.TrailingZeroCount(_b);
            if (_c != 0) return 128 + System.Numerics.BitOperations.TrailingZeroCount(_c);
            if (_d != 0) return 192 + System.Numerics.BitOperations.TrailingZeroCount(_d);
            for (int i = 0; ; i++)
                if (_tail![i] != 0) return 256 + i * 64 + System.Numerics.BitOperations.TrailingZeroCount(_tail[i]);
        }
    }

    internal readonly int Count
    {
        get
        {
            int count = System.Numerics.BitOperations.PopCount(_a) + System.Numerics.BitOperations.PopCount(_b)
                + System.Numerics.BitOperations.PopCount(_c) + System.Numerics.BitOperations.PopCount(_d);
            if (_tail is { } tail) foreach (ulong word in tail) count += System.Numerics.BitOperations.PopCount(word);
            return count;
        }
    }

    internal readonly ComponentBits Copy()
        => new() { _a = _a, _b = _b, _c = _c, _d = _d, _tail = _tail?.ToArray() };

    internal readonly bool Equals(in ComponentBits other)
        => _a == other._a && _b == other._b && _c == other._c && _d == other._d
            && _tail.AsSpan().SequenceEqual(other._tail);

    internal readonly void AddHash(ref HashCode hash)
    {
        hash.Add(_a); hash.Add(_b); hash.Add(_c); hash.Add(_d);
        if (_tail is { } tail) foreach (ulong word in tail) hash.Add(word);
    }

    internal readonly bool Has(int type)
    {
        ulong word = (type >> 6) switch
        {
            0 => _a, 1 => _b, 2 => _c, 3 => _d,
            _ => _tail is { } tail && (type >> 6) - 4 < tail.Length ? tail[(type >> 6) - 4] : 0,
        };
        return (word & (1UL << (type & 63))) != 0;
    }

    internal readonly bool IntersectsExcept(in ComponentBits other, in ComponentBits except)
    {
        if (((_a & other._a & ~except._a) | (_b & other._b & ~except._b) |
            (_c & other._c & ~except._c) | (_d & other._d & ~except._d)) != 0) return true;
        if (_tail is { } left && other._tail is { } right)
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                ulong removed = except._tail is { } tail && i < tail.Length ? tail[i] : 0;
                if ((left[i] & right[i] & ~removed) != 0) return true;
            }
        return false;
    }

    internal void Add(int type)
    {
        ulong bit = 1UL << (type & 63);
        switch (type >> 6)
        {
            case 0: _a |= bit; break;
            case 1: _b |= bit; break;
            case 2: _c |= bit; break;
            case 3: _d |= bit; break;
            default:
                int word = (type >> 6) - 4;
                if (_tail is null || word >= _tail.Length) Array.Resize(ref _tail, word + 1);
                _tail[word] |= bit;
                break;
        }
    }

    internal void Remove(int type)
    {
        ulong mask = ~(1UL << (type & 63));
        switch (type >> 6)
        {
            case 0: _a &= mask; break;
            case 1: _b &= mask; break;
            case 2: _c &= mask; break;
            case 3: _d &= mask; break;
            default:
                if (_tail is { } tail && (type >> 6) - 4 < tail.Length)
                    tail[(type >> 6) - 4] &= mask;
                break;
        }
    }

    internal readonly bool Contains(in ComponentBits required)
    {
        if ((_a & required._a) != required._a || (_b & required._b) != required._b ||
            (_c & required._c) != required._c || (_d & required._d) != required._d)
            return false;
        if (required._tail is { } words)
            for (int i = 0; i < words.Length; i++)
                if (words[i] != 0 && (_tail is null || i >= _tail.Length || (_tail[i] & words[i]) != words[i]))
                    return false;
        return true;
    }

    internal readonly bool Intersects(in ComponentBits other)
    {
        if (((_a & other._a) | (_b & other._b) | (_c & other._c) | (_d & other._d)) != 0)
            return true;
        if (_tail is { } left && other._tail is { } right)
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
                if ((left[i] & right[i]) != 0) return true;
        return false;
    }
}
