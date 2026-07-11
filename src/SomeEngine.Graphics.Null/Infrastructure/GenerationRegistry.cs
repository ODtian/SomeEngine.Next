namespace SomeEngine.Graphics.Null;

internal static class HandleGeneration
{
    private static int s_next;

    public static uint Next()
    {
        while (true)
        {
            uint value = unchecked((uint)Interlocked.Increment(ref s_next));
            if (value != 0) return value;
        }
    }
}

internal struct CompletionSet
{
    public ulong Graphics;
    public ulong Compute;
    public ulong Copy;

    public void Mark(QueueType queue, ulong value)
    {
        switch (queue)
        {
            case QueueType.Graphics:
                Graphics = Math.Max(Graphics, value);
                break;
            case QueueType.Compute:
                Compute = Math.Max(Compute, value);
                break;
            case QueueType.Copy:
                Copy = Math.Max(Copy, value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(queue));
        }
    }

    public readonly bool HasCompleted(ReadOnlySpan<ulong> completed) =>
        completed.Length >= 3 &&
        completed[(int)QueueType.Graphics] >= Graphics &&
        completed[(int)QueueType.Compute] >= Compute &&
        completed[(int)QueueType.Copy] >= Copy;
}

internal sealed class GenerationRegistry<T> where T : class
{
    internal sealed class Slot
    {
        public uint Generation;
        public bool Alive;
        public bool PendingRetirement;
        public T? Value;
        public CompletionSet LastUse;
        public int PendingUseCount;
        public int LiveChildCount;
    }

    private readonly string _kind;
    private readonly DeviceDomain _domain;
    private readonly List<Slot> _slots = [new Slot()];
    private readonly Stack<uint> _free = new();

    public GenerationRegistry(DeviceDomain domain, string kind)
    {
        if (!domain.IsValid) throw new ArgumentException("A valid device domain is required.", nameof(domain));
        _domain = domain;
        _kind = kind;
    }

    public (uint Slot, uint Generation) Allocate(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        uint index;
        Slot slot;
        if (_free.Count != 0)
        {
            index = _free.Pop();
            slot = _slots[checked((int)index)];
        }
        else
        {
            index = checked((uint)_slots.Count);
            slot = new Slot();
            _slots.Add(slot);
        }

        slot.Generation = HandleGeneration.Next();
        slot.Alive = true;
        slot.PendingRetirement = false;
        slot.Value = value;
        slot.LastUse = default;
        slot.PendingUseCount = 0;
        slot.LiveChildCount = 0;
        return (index, slot.Generation);
    }

    public Slot RequireAlive(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireOccupied(domain, index, generation);
        if (!slot.Alive)
        {
            throw new InvalidOperationException($"{_kind} handle ({index}, {generation}) is stale or destroyed.");
        }

        return slot;
    }

    public Slot RequireOccupied(DeviceDomain domain, uint index, uint generation)
    {
        RequireDomain(domain);
        if (index == 0 || generation == 0 || index >= (uint)_slots.Count)
        {
            throw new ArgumentException($"Invalid or cross-device {_kind} handle ({index}, {generation}).");
        }

        Slot slot = _slots[checked((int)index)];
        if (slot.Value is null || slot.Generation != generation)
        {
            throw new ArgumentException($"Invalid, stale, or cross-device {_kind} handle ({index}, {generation}).");
        }

        return slot;
    }

    public void Pin(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireAlive(domain, index, generation);
        slot.PendingUseCount = checked(slot.PendingUseCount + 1);
    }

    public void CancelPin(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireAlive(domain, index, generation);
        if (slot.PendingUseCount <= 0) throw new InvalidOperationException($"{_kind} pending-use count underflow.");
        slot.PendingUseCount--;
    }

    public void SubmitPin(DeviceDomain domain, uint index, uint generation, QueueType queue, ulong value)
    {
        Slot slot = RequireAlive(domain, index, generation);
        if (slot.PendingUseCount <= 0) throw new InvalidOperationException($"{_kind} was submitted without an unpublished-use pin.");
        slot.PendingUseCount--;
        slot.LastUse.Mark(queue, value);
    }

    public void MarkUsed(DeviceDomain domain, uint index, uint generation, QueueType queue, ulong value) =>
        RequireAlive(domain, index, generation).LastUse.Mark(queue, value);

    public void AddChild(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireAlive(domain, index, generation);
        slot.LiveChildCount = checked(slot.LiveChildCount + 1);
    }

    public void ReleaseChild(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireAlive(domain, index, generation);
        if (slot.LiveChildCount <= 0) throw new InvalidOperationException($"{_kind} live-child count underflow.");
        slot.LiveChildCount--;
    }

    public bool HasCompletedLastUse(DeviceDomain domain, uint index, uint generation, ReadOnlySpan<ulong> completed) =>
        RequireAlive(domain, index, generation).LastUse.HasCompleted(completed);

    public void Destroy(DeviceDomain domain, uint index, uint generation)
    {
        Slot slot = RequireAlive(domain, index, generation);
        if (slot.PendingUseCount != 0)
        {
            throw new InvalidOperationException($"A {_kind} referenced by an unpublished command list cannot be destroyed.");
        }
        if (slot.LiveChildCount != 0)
        {
            throw new InvalidOperationException($"A {_kind} with live child objects cannot be destroyed.");
        }
        slot.Alive = false;
        slot.PendingRetirement = true;
    }

    public int Collect(ReadOnlySpan<ulong> completed)
    {
        int retired = 0;
        for (int index = 1; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            if (!slot.PendingRetirement || !slot.LastUse.HasCompleted(completed)) continue;
            slot.Value = null;
            slot.PendingRetirement = false;
            slot.LastUse = default;
            slot.PendingUseCount = 0;
            slot.LiveChildCount = 0;
            _free.Push(checked((uint)index));
            retired++;
        }

        return retired;
    }

    public IEnumerable<(uint Index, Slot Slot)> Occupied()
    {
        for (int index = 1; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            if (slot.Value is not null) yield return (checked((uint)index), slot);
        }
    }

    public void Clear()
    {
        for (int index = 1; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            slot.Value = null;
            slot.Alive = false;
            slot.PendingRetirement = false;
            slot.LastUse = default;
            slot.PendingUseCount = 0;
            slot.LiveChildCount = 0;
        }

        _free.Clear();
    }

    private void RequireDomain(DeviceDomain domain)
    {
        if (domain != _domain)
        {
            throw new ArgumentException($"Invalid or cross-device {_kind} handle.", nameof(domain));
        }
    }
}
