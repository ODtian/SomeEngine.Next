using System.Runtime.InteropServices;

namespace SomeEngine.Job;

/// <summary>
/// Bounded Vyukov MPMC ring used as the cross-thread entry to worker-local Chase-Lev deques.
/// Sequence numbers publish each slot and prevent wrapped producers from observing stale data.
/// </summary>
internal sealed class MpmcInjector<T>
    where T : struct
{
    private readonly Slot[] _slots;
    private readonly int _mask;
    private long _enqueuePosition;
    private long _dequeuePosition;

    internal MpmcInjector(int minimumCapacity)
    {
        int capacity = RoundUpPowerOfTwo(Math.Max(2, minimumCapacity));
        _slots = new Slot[capacity];
        _mask = capacity - 1;
        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Sequence = i;
    }

    internal bool IsEmpty =>
        Volatile.Read(ref _dequeuePosition) >= Volatile.Read(ref _enqueuePosition);

    internal bool TryEnqueue(in T item)
    {
        while (true)
        {
            long position = Volatile.Read(ref _enqueuePosition);
            ref Slot slot = ref _slots[(int)position & _mask];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - position;
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref _enqueuePosition,
                        position + 1,
                        position) != position)
                {
                    continue;
                }

                slot.Item = item;
                Volatile.Write(ref slot.Sequence, position + 1);
                return true;
            }

            if (difference < 0)
                return false;
        }
    }

    internal bool TryDequeue(out T item)
    {
        while (true)
        {
            long position = Volatile.Read(ref _dequeuePosition);
            ref Slot slot = ref _slots[(int)position & _mask];
            long sequence = Volatile.Read(ref slot.Sequence);
            long difference = sequence - (position + 1);
            if (difference == 0)
            {
                if (Interlocked.CompareExchange(
                        ref _dequeuePosition,
                        position + 1,
                        position) != position)
                {
                    continue;
                }

                item = slot.Item;
                slot.Item = default;
                Volatile.Write(ref slot.Sequence, position + _slots.Length);
                return true;
            }

            if (difference < 0)
            {
                item = default;
                return false;
            }
        }
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

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct Slot
    {
        internal long Sequence;
        internal T Item;
    }
}
