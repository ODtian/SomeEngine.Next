using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

/// <summary>
/// Per-World relation type metadata indexed by the payload component ID. Registration is
/// serialized by <see cref="RelationGraph"/>; readers observe either the previous published
/// array or the complete replacement without taking that lock.
/// </summary>
internal sealed class RelationTypeSlotTable
{
    private IRelationTypeState?[] _slots = Array.Empty<IRelationTypeState?>();
    private int _count;

    internal int Count => Volatile.Read(ref _count);

    internal bool TryGetValue(int componentId, out IRelationTypeState state)
    {
        IRelationTypeState?[] slots = Volatile.Read(ref _slots);
        if ((uint)componentId < (uint)slots.Length &&
            Volatile.Read(ref slots[componentId]) is IRelationTypeState found)
        {
            state = found;
            return true;
        }

        state = null!;
        return false;
    }

    internal IRelationTypeState this[int componentId] =>
        TryGetValue(componentId, out IRelationTypeState state)
            ? state
            : throw new KeyNotFoundException(
                $"No relation type state is registered for component ID {componentId}.");

    internal void Add(int componentId, IRelationTypeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (componentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(componentId));

        IRelationTypeState?[] slots = Volatile.Read(ref _slots);
        if ((uint)componentId < (uint)slots.Length &&
            Volatile.Read(ref slots[componentId]) is not null)
        {
            throw new InvalidOperationException(
                $"Relation type state component ID {componentId} is already registered.");
        }

        var grown = new IRelationTypeState?[GrowthLength(slots.Length, componentId + 1)];
        slots.CopyTo(grown, 0);
        grown[componentId] = state;
        Volatile.Write(ref _slots, grown);

        Volatile.Write(ref _count, checked(Volatile.Read(ref _count) + 1));
    }

    public Enumerator GetEnumerator() => new(Volatile.Read(ref _slots));

    internal IRelationTypeState[] SnapshotValues()
    {
        IRelationTypeState?[] slots = Volatile.Read(ref _slots);
        var values = new IRelationTypeState[CountOccupied(slots)];
        int destination = 0;
        for (int componentId = 1; componentId < slots.Length; componentId++)
        {
            if (Volatile.Read(ref slots[componentId]) is IRelationTypeState state)
                values[destination++] = state;
        }

        return values;
    }

    internal void CloneDetachedInto(RelationTypeSlotTable destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Count != 0)
            throw new InvalidOperationException("Relation type clone destination must be empty.");

        IRelationTypeState?[] source = Volatile.Read(ref _slots);
        var cloned = new IRelationTypeState?[source.Length];
        int count = 0;
        for (int componentId = 1; componentId < source.Length; componentId++)
        {
            if (Volatile.Read(ref source[componentId]) is not IRelationTypeState state)
                continue;

            cloned[componentId] = state.CloneDetached();
            count++;
        }

        Volatile.Write(ref destination._slots, cloned);
        Volatile.Write(ref destination._count, count);
    }

    internal void Clear()
    {
        Volatile.Write(ref _slots, Array.Empty<IRelationTypeState?>());
        Volatile.Write(ref _count, 0);
    }

    private static int CountOccupied(IRelationTypeState?[] slots)
    {
        int count = 0;
        for (int componentId = 1; componentId < slots.Length; componentId++)
        {
            if (Volatile.Read(ref slots[componentId]) is not null)
                count++;
        }
        return count;
    }

    private static int GrowthLength(int current, int required)
    {
        if (current >= required)
            return current;
        int doubled = current == 0 ? 8 : checked(current * 2);
        return Math.Max(doubled, required);
    }

    internal struct Enumerator
    {
        private readonly ReadOnlyMemory<IRelationTypeState?> _slots;
        private int _index;

        internal Enumerator(ReadOnlyMemory<IRelationTypeState?> slots)
        {
            _slots = slots;
            _index = 0;
            Current = null!;
        }

        public IRelationTypeState Current { get; private set; }

        public bool MoveNext()
        {
            ReadOnlySpan<IRelationTypeState?> slots = _slots.Span;
            while (++_index < slots.Length)
            {
                if (slots[_index] is IRelationTypeState state)
                {
                    Current = state;
                    return true;
                }
            }

            Current = null!;
            return false;
        }
    }
}

/// <summary>
/// Published component-ID slot table for auxiliary relation metadata.
/// </summary>
internal sealed class RelationComponentSlotTable<TValue>
    where TValue : class
{
    private TValue?[] _slots = Array.Empty<TValue?>();
    private int _count;

    internal int Count => Volatile.Read(ref _count);

    internal bool TryGetValue(int componentId, out TValue value)
    {
        TValue?[] slots = Volatile.Read(ref _slots);
        if ((uint)componentId < (uint)slots.Length &&
            Volatile.Read(ref slots[componentId]) is TValue found)
        {
            value = found;
            return true;
        }

        value = null!;
        return false;
    }

    internal TValue this[int componentId] =>
        TryGetValue(componentId, out TValue value)
            ? value
            : throw new KeyNotFoundException(
                $"No relation metadata is registered for component ID {componentId}.");

    internal void Add(int componentId, TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (componentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(componentId));

        TValue?[] slots = Volatile.Read(ref _slots);
        if ((uint)componentId < (uint)slots.Length &&
            Volatile.Read(ref slots[componentId]) is not null)
        {
            throw new InvalidOperationException(
                $"Relation metadata component ID {componentId} is already registered.");
        }

        var grown = new TValue?[GrowthLength(slots.Length, componentId + 1)];
        slots.CopyTo(grown, 0);
        grown[componentId] = value;
        Volatile.Write(ref _slots, grown);

        Volatile.Write(ref _count, checked(Volatile.Read(ref _count) + 1));
    }

    public Enumerator GetEnumerator() => new(Volatile.Read(ref _slots));

    internal void Clear()
    {
        Volatile.Write(ref _slots, Array.Empty<TValue?>());
        Volatile.Write(ref _count, 0);
    }

    private static int GrowthLength(int current, int required)
    {
        if (current >= required)
            return current;
        int doubled = current == 0 ? 8 : checked(current * 2);
        return Math.Max(doubled, required);
    }

    internal struct Enumerator
    {
        private readonly ReadOnlyMemory<TValue?> _slots;
        private int _index;

        internal Enumerator(ReadOnlyMemory<TValue?> slots)
        {
            _slots = slots;
            _index = 0;
            Current = null!;
        }

        public TValue Current { get; private set; }

        public bool MoveNext()
        {
            ReadOnlySpan<TValue?> slots = _slots.Span;
            while (++_index < slots.Length)
            {
                if (slots[_index] is TValue value)
                {
                    Current = value;
                    return true;
                }
            }

            Current = null!;
            return false;
        }
    }
}
