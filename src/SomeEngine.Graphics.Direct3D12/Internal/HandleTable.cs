namespace SomeEngine.Graphics.Direct3D12;

internal readonly record struct HandleKey(uint Slot, uint Generation);

internal sealed class HandleTable<T> where T : class
{
    private readonly object _gate = new();
    private readonly DeviceDomain _domain;
    private readonly List<Entry> _entries = [new Entry(null, 0)];
    private readonly Stack<uint> _free = new();

    public HandleTable(DeviceDomain domain)
    {
        if (!domain.IsValid) throw new ArgumentException("A valid device domain is required.", nameof(domain));
        _domain = domain;
    }

    public HandleKey Add(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            uint slot;
            uint generation;
            if (_free.TryPop(out slot))
            {
                Entry previous = _entries[checked((int)slot)];
                generation = previous.Generation == 0 ? 1 : previous.Generation;
                _entries[checked((int)slot)] = new Entry(value, generation);
            }
            else
            {
                slot = checked((uint)_entries.Count);
                generation = 1;
                _entries.Add(new Entry(value, generation));
            }

            return new HandleKey(slot, generation);
        }
    }

    public T Get(DeviceDomain domain, uint slot, uint generation, string kind)
    {
        lock (_gate)
        {
            RequireDomain(domain, kind);
            if (slot == 0 || slot >= _entries.Count)
            {
                throw new ArgumentException($"Invalid {kind} handle {slot}:{generation}.");
            }

            Entry entry = _entries[checked((int)slot)];
            if (entry.Value is null || entry.Generation != generation)
            {
                throw new ArgumentException($"Stale {kind} handle {slot}:{generation}.");
            }

            return entry.Value;
        }
    }

    public T Remove(DeviceDomain domain, uint slot, uint generation, string kind)
    {
        lock (_gate)
        {
            RequireDomain(domain, kind);
            T value = GetWithoutLock(slot, generation, kind);
            uint next = unchecked(generation + 1);
            if (next == 0) next = 1;
            _entries[checked((int)slot)] = new Entry(null, next);
            _free.Push(slot);
            return value;
        }
    }

    public T[] Drain()
    {
        lock (_gate)
        {
            List<T> values = new();
            for (int index = 1; index < _entries.Count; index++)
            {
                Entry entry = _entries[index];
                if (entry.Value is not null) values.Add(entry.Value);
                uint next = unchecked(entry.Generation + 1);
                if (next == 0) next = 1;
                _entries[index] = new Entry(null, next);
            }
            _free.Clear();
            for (int index = _entries.Count - 1; index >= 1; index--) _free.Push(checked((uint)index));
            return values.ToArray();
        }
    }

    private T GetWithoutLock(uint slot, uint generation, string kind)
    {
        if (slot == 0 || slot >= _entries.Count)
        {
            throw new ArgumentException($"Invalid {kind} handle {slot}:{generation}.");
        }

        Entry entry = _entries[checked((int)slot)];
        if (entry.Value is null || entry.Generation != generation)
        {
            throw new ArgumentException($"Stale {kind} handle {slot}:{generation}.");
        }
        return entry.Value;
    }

    private void RequireDomain(DeviceDomain domain, string kind)
    {
        if (domain != _domain) throw new ArgumentException($"Invalid or cross-device {kind} handle.", nameof(domain));
    }

    private readonly record struct Entry(T? Value, uint Generation);
}
