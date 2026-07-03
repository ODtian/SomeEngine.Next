using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal abstract class WorkStream
{
    internal abstract bool MakeReady(int slot, int maxDrainers, int priorityIndex);

    internal abstract int MakeReady(ReadOnlySpan<int> slots, int count, int maxDrainers, int priorityIndex);

    internal abstract void Cancel(int slot);

    internal abstract int DrainAndFinish(
        Scheduler scheduler,
        int maxItems,
        int priorityIndex,
        int maxDrainers,
        out bool hasMoreWork);
}

internal interface IWorkStreamItem<TSelf>
    where TSelf : struct, IWorkStreamItem<TSelf>
{
    static abstract JobHandle GetState(in TSelf item);

    static abstract void Execute(ref TSelf item);

    static abstract void Release(ref TSelf item);
}

internal interface IWorkStreamItemSource<TItem>
    where TItem : struct, IWorkStreamItem<TItem>
{
    TItem Create(int index);
}

internal sealed class WorkStream<TItem> : WorkStream
    where TItem : struct, IWorkStreamItem<TItem>
{
    private const int DrainQuantum = 64;
    private static readonly bool ContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<TItem>();

    internal static WorkStream<TItem> Instance { get; } = new();

    private readonly Lock _sync = new();
    private readonly List<Slot> _slots = [default];
    private readonly Stack<int> _freeSlots = new();
    private readonly Queue<int>[] _readySlots;
    private readonly int[] _firstReadySlots = new int[JobPriorityOrder.Count];
    private readonly int[] _scheduledDrainers = new int[JobPriorityOrder.Count];
    private int _firstFreeSlot;

    [ThreadStatic]
    private static TItem[]? s_drainBuffer;

    private WorkStream()
    {
        _readySlots = new Queue<int>[JobPriorityOrder.Count];
        for (int i = 0; i < _readySlots.Length; i++)
        {
            _readySlots[i] = new Queue<int>();
        }
    }

    internal int Prepare(in TItem item)
    {
        lock (_sync)
        {
            int slotIndex = RentSlotIndex();
            Slot slot = _slots[slotIndex];
            slot.InUse = true;
            slot.Ready = false;
            slot.Item = item;
            _slots[slotIndex] = slot;
            return slotIndex;
        }
    }

    internal int PrepareReady(in TItem item, int maxDrainers, int priorityIndex)
    {
        lock (_sync)
        {
            int slotIndex = RentSlotIndex();
            Slot slot = _slots[slotIndex];
            slot.InUse = true;
            slot.Ready = true;
            slot.Item = item;
            _slots[slotIndex] = slot;
            EnqueueReadySlot(slotIndex, priorityIndex);

            if (!CanScheduleAnotherDrainer(maxDrainers, priorityIndex))
            {
                return 0;
            }

            _scheduledDrainers[priorityIndex]++;
            return 1;
        }
    }

    internal int PrepareMany<TSource>(Span<int> slots, ref TSource source)
        where TSource : struct, IWorkStreamItemSource<TItem>
    {
        lock (_sync)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                int slotIndex = RentSlotIndex();
                Slot slot = _slots[slotIndex];
                slot.InUse = true;
                slot.Ready = false;
                slot.Item = source.Create(i);
                _slots[slotIndex] = slot;
                slots[i] = slotIndex;
            }

            return slots.Length;
        }
    }

    internal int PrepareManyReady<TSource>(int count, ref TSource source, int maxDrainers, int priorityIndex)
        where TSource : struct, IWorkStreamItemSource<TItem>
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_sync)
        {
            bool useFirstReadySlot = count == 1;
            for (int i = 0; i < count; i++)
            {
                int slotIndex = RentSlotIndex();
                Slot slot = _slots[slotIndex];
                slot.InUse = true;
                slot.Ready = true;
                slot.Item = source.Create(i);
                _slots[slotIndex] = slot;
                if (useFirstReadySlot)
                {
                    EnqueueReadySlot(slotIndex, priorityIndex);
                }
                else
                {
                    _readySlots[priorityIndex].Enqueue(slotIndex);
                }
            }

            int drainerCount = 0;
            while (drainerCount < count && CanScheduleAnotherDrainer(maxDrainers, priorityIndex))
            {
                _scheduledDrainers[priorityIndex]++;
                drainerCount++;
            }

            return drainerCount;
        }
    }

    internal override bool MakeReady(int slot, int maxDrainers, int priorityIndex)
    {
        lock (_sync)
        {
            if (slot <= 0 || slot >= _slots.Count)
            {
                return false;
            }

            Slot entry = _slots[slot];
            if (!entry.InUse || entry.Ready)
            {
                return false;
            }

            entry.Ready = true;
            _slots[slot] = entry;
            EnqueueReadySlot(slot, priorityIndex);
            if (!CanScheduleAnotherDrainer(maxDrainers, priorityIndex))
            {
                return false;
            }

            _scheduledDrainers[priorityIndex]++;
            return true;
        }
    }

    internal override int MakeReady(ReadOnlySpan<int> slots, int count, int maxDrainers, int priorityIndex)
    {
        if ((uint)count > (uint)slots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_sync)
        {
            int readyCount = 0;
            bool useFirstReadySlot = count == 1;
            for (int i = 0; i < count; i++)
            {
                int slot = slots[i];
                if (slot <= 0 || slot >= _slots.Count)
                {
                    continue;
                }

                Slot entry = _slots[slot];
                if (!entry.InUse || entry.Ready)
                {
                    continue;
                }

                entry.Ready = true;
                _slots[slot] = entry;
                if (useFirstReadySlot)
                {
                    EnqueueReadySlot(slot, priorityIndex);
                }
                else
                {
                    _readySlots[priorityIndex].Enqueue(slot);
                }
                readyCount++;
            }

            int drainerCount = 0;
            while (drainerCount < readyCount && CanScheduleAnotherDrainer(maxDrainers, priorityIndex))
            {
                _scheduledDrainers[priorityIndex]++;
                drainerCount++;
            }

            return drainerCount;
        }
    }

    internal override void Cancel(int slot)
    {
        lock (_sync)
        {
            if (slot <= 0 || slot >= _slots.Count)
            {
                return;
            }

            Slot entry = _slots[slot];
            if (!entry.InUse)
            {
                return;
            }

            TItem.Release(ref entry.Item);
            ReleaseSlot(slot, ref entry);
        }
    }

    internal override int DrainAndFinish(
        Scheduler scheduler,
        int maxItems,
        int priorityIndex,
        int maxDrainers,
        out bool hasMoreWork)
    {
        BeginDrain(priorityIndex);
        int claimed = Drain(scheduler, maxItems, priorityIndex);
        hasMoreWork = FinishDrain(maxDrainers, priorityIndex);
        return claimed;
    }

    private int Drain(Scheduler scheduler, int maxItems, int priorityIndex)
    {
        int claimed = 0;
        int limit = maxItems <= 0 ? DrainQuantum : maxItems;
        if (!TryTakeReady(out TItem item, out bool hasMoreReady, priorityIndex))
        {
            return 0;
        }

        claimed++;
        scheduler.ExecuteStreamItem(ref item);
        if (hasMoreReady && claimed < limit)
        {
            claimed += DrainReadyBatch(scheduler, limit - claimed, priorityIndex);
        }

        return claimed;
    }

    private int DrainReadyBatch(Scheduler scheduler, int limit, int priorityIndex)
    {
        TItem[]? items = s_drainBuffer;
        if (items is null || items.Length < limit)
        {
            items = new TItem[Math.Max(limit, DrainQuantum)];
            s_drainBuffer = items;
        }

        int claimed = TakeReadyBatch(items, limit, priorityIndex);
        try
        {
            for (int i = 0; i < claimed; i++)
            {
                scheduler.ExecuteStreamItem(ref items[i]);
            }
        }
        finally
        {
            if (ContainsReferences)
            {
                Array.Clear(items, 0, claimed);
            }
        }

        return claimed;
    }

    private void BeginDrain(int priorityIndex)
    {
        lock (_sync)
        {
            if (_scheduledDrainers[priorityIndex] > 0)
            {
                _scheduledDrainers[priorityIndex]--;
            }
        }
    }

    private bool FinishDrain(int maxDrainers, int priorityIndex)
    {
        lock (_sync)
        {
            if (CanScheduleAnotherDrainer(maxDrainers, priorityIndex))
            {
                _scheduledDrainers[priorityIndex]++;
                return true;
            }

            return false;
        }
    }

    private bool CanScheduleAnotherDrainer(int maxDrainers, int priorityIndex)
    {
        int limit = Math.Max(1, maxDrainers);
        return HasReadySlots(priorityIndex) && _scheduledDrainers[priorityIndex] < limit;
    }

    private bool HasReadySlots(int priorityIndex)
    {
        return _firstReadySlots[priorityIndex] != 0 || _readySlots[priorityIndex].Count != 0;
    }

    private void EnqueueReadySlot(int slot, int priorityIndex)
    {
        if (_firstReadySlots[priorityIndex] == 0)
        {
            _firstReadySlots[priorityIndex] = slot;
            return;
        }

        _readySlots[priorityIndex].Enqueue(slot);
    }

    private bool TryDequeueReadySlot(out int slot, int priorityIndex)
    {
        if (_firstReadySlots[priorityIndex] != 0)
        {
            slot = _firstReadySlots[priorityIndex];
            _firstReadySlots[priorityIndex] = _readySlots[priorityIndex].Count != 0
                ? _readySlots[priorityIndex].Dequeue()
                : 0;
            return true;
        }

        if (_readySlots[priorityIndex].Count != 0)
        {
            slot = _readySlots[priorityIndex].Dequeue();
            return true;
        }

        slot = 0;
        return false;
    }

    private int RentSlotIndex()
    {
        if (_firstFreeSlot != 0)
        {
            int cachedSlot = _firstFreeSlot;
            _firstFreeSlot = _freeSlots.Count != 0 ? _freeSlots.Pop() : 0;
            return cachedSlot;
        }

        int slotIndex = _slots.Count;
        _slots.Add(default);
        return slotIndex;
    }

    private bool TryTakeReady(out TItem item, out bool hasMoreReady, int priorityIndex)
    {
        lock (_sync)
        {
            if (_firstReadySlots[priorityIndex] == 0)
            {
                while (_readySlots[priorityIndex].Count > 0)
                {
                    int slotIndex = _readySlots[priorityIndex].Dequeue();
                    if (slotIndex <= 0 || slotIndex >= _slots.Count)
                    {
                        continue;
                    }

                    Slot slot = _slots[slotIndex];
                    if (!slot.InUse || !slot.Ready)
                    {
                        continue;
                    }

                    item = slot.Item;
                    ReleaseSlot(slotIndex, ref slot);
                    hasMoreReady = _readySlots[priorityIndex].Count > 0;
                    return true;
                }
            }

            while (TryDequeueReadySlot(out int slotIndex, priorityIndex))
            {
                if (slotIndex <= 0 || slotIndex >= _slots.Count)
                {
                    continue;
                }

                Slot slot = _slots[slotIndex];
                if (!slot.InUse || !slot.Ready)
                {
                    continue;
                }

                item = slot.Item;
                ReleaseSlot(slotIndex, ref slot);
                hasMoreReady = HasReadySlots(priorityIndex);
                return true;
            }
        }

        item = default;
        hasMoreReady = false;
        return false;
    }

    private int TakeReadyBatch(TItem[] items, int limit, int priorityIndex)
    {
        int claimed = 0;
        lock (_sync)
        {
            if (_firstReadySlots[priorityIndex] == 0)
                return TakeQueuedReadySlots(items, limit, priorityIndex);

            while (claimed < limit && TryDequeueReadySlot(out var slotIndex, priorityIndex))
            {
                if (TryClaimReadySlot(slotIndex, out var item))
                    items[claimed++] = item;
            }
        }

        return claimed;
    }

    private int TakeQueuedReadySlots(TItem[] items, int limit, int priorityIndex)
    {
        int claimed = 0;
        while (claimed < limit && _readySlots[priorityIndex].Count > 0)
        {
            int slotIndex = _readySlots[priorityIndex].Dequeue();
            if (TryClaimReadySlot(slotIndex, out var item))
                items[claimed++] = item;
        }

        return claimed;
    }

    private bool TryClaimReadySlot(int slotIndex, out TItem item)
    {
        if (slotIndex <= 0 || slotIndex >= _slots.Count)
        {
            item = default!;
            return false;
        }

        Slot slot = _slots[slotIndex];
        if (!slot.InUse || !slot.Ready)
        {
            item = default!;
            return false;
        }

        item = slot.Item;
        ReleaseSlot(slotIndex, ref slot);
        return true;
    }

    private void ReleaseSlot(int slotIndex, ref Slot slot)
    {
        slot.InUse = false;
        slot.Ready = false;
        if (ContainsReferences)
        {
            slot.Item = default;
        }

        _slots[slotIndex] = slot;
        if (_firstFreeSlot == 0)
        {
            _firstFreeSlot = slotIndex;
        }
        else
        {
            _freeSlots.Push(slotIndex);
        }
    }

    private struct Slot
    {
        internal TItem Item;
        internal bool InUse;
        internal bool Ready;
    }
}

internal struct ScheduledJob<T> : IWorkStreamItem<ScheduledJob<T>>
    where T : struct, IJob
{
    private T _job;
    private JobHandle _state;

    internal ScheduledJob(in T job, JobHandle state)
    {
        _job = job;
        _state = state;
    }

    public static JobHandle GetState(in ScheduledJob<T> item)
    {
        return item._state;
    }

    public static void Execute(ref ScheduledJob<T> item)
    {
        item._job.Execute();
    }

    public static void Release(ref ScheduledJob<T> item)
    {
        item = default;
    }
}

internal readonly struct ScheduledParallelJobSource<T> : IWorkStreamItemSource<ScheduledParallelJob<T>>
    where T : struct, IJobParallelFor
{
    private readonly T _job;
    private readonly JobHandle _state;
    private readonly int _length;
    private readonly int _batchSize;

    internal ScheduledParallelJobSource(in T job, JobHandle state, int length, int batchSize)
    {
        _job = job;
        _state = state;
        _length = length;
        _batchSize = batchSize;
    }

    public ScheduledParallelJob<T> Create(int batchIndex)
    {
        int start = batchIndex * _batchSize;
        int end = Math.Min(start + _batchSize, _length);
        return new ScheduledParallelJob<T>(_job, _state, start, end);
    }
}

internal struct ScheduledParallelJob<T> : IWorkStreamItem<ScheduledParallelJob<T>>
    where T : struct, IJobParallelFor
{
    private T _job;
    private readonly int _start;
    private readonly int _end;
    private JobHandle _state;

    internal ScheduledParallelJob(
        in T job,
        JobHandle state,
        int start,
        int end)
    {
        _job = job;
        _state = state;
        _start = start;
        _end = end;
    }

    public static JobHandle GetState(in ScheduledParallelJob<T> item)
    {
        return item._state;
    }

    public static void Execute(ref ScheduledParallelJob<T> item)
    {
        for (int index = item._start; index < item._end; index++)
        {
            item._job.Execute(index);
        }
    }

    public static void Release(ref ScheduledParallelJob<T> item)
    {
        item = default;
    }
}



