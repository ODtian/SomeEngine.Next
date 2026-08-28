using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal abstract class WorkStream
{
    internal abstract int MakeReady(int slot, int maxDrainers, int priorityIndex);

    internal abstract int MakeReady(ReadOnlySpan<int> slots, int count, int maxDrainers, int priorityIndex);

    internal abstract void Cancel(int slot);

    internal abstract int DrainAndFinish(
        Scheduler scheduler,
        int maxItems,
        int priorityIndex,
        int maxDrainers,
        out int retiredItems,
        out bool hasMoreWork);
}

internal readonly struct WorkItemExecutionResult
{
    internal WorkItemExecutionResult(
        bool retire,
        int completedWorkItems,
        int faultedWorkItems = 0,
        ExceptionDispatchInfo? fault = null)
    {
        Retire = retire;
        CompletedWorkItems = completedWorkItems;
        FaultedWorkItems = faultedWorkItems;
        Fault = fault;
    }

    internal bool Retire { get; }

    internal int CompletedWorkItems { get; }

    internal int FaultedWorkItems { get; }

    internal ExceptionDispatchInfo? Fault { get; }
}

internal interface IWorkStreamItem<TSelf>
    where TSelf : struct, IWorkStreamItem<TSelf>
{
    static abstract JobHandle GetState(in TSelf item);

    static abstract bool AllowBatchDrain { get; }

    static abstract WorkItemExecutionResult Execute(ref TSelf item);

    static abstract int Abandon(ref TSelf item);

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
    private readonly ConcurrentQueue<TItem>[] _readyItems;
    private readonly int[] _scheduledDrainers = new int[JobPriorityOrder.Count];
    private int _firstFreeSlot;

    [ThreadStatic]
    private static TItem[]? s_drainBuffer;

    private WorkStream()
    {
        _readyItems = new ConcurrentQueue<TItem>[JobPriorityOrder.Count];
        for (int i = 0; i < _readyItems.Length; i++)
        {
            _readyItems[i] = new ConcurrentQueue<TItem>();
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
        _readyItems[priorityIndex].Enqueue(item);
        return TryReserveDrainer(maxDrainers, priorityIndex) ? 1 : 0;
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

        for (int i = 0; i < count; i++)
            _readyItems[priorityIndex].Enqueue(source.Create(i));

        int drainerCount = 0;
        while (drainerCount < count && TryReserveDrainer(maxDrainers, priorityIndex))
        {
            drainerCount++;
        }

        return drainerCount;
    }

    internal override int MakeReady(int slot, int maxDrainers, int priorityIndex)
    {
        TItem item;
        lock (_sync)
        {
            if (slot <= 0 || slot >= _slots.Count)
            {
                return 0;
            }

            Slot entry = _slots[slot];
            if (!entry.InUse)
            {
                return 0;
            }

            item = entry.Item;
            ReleaseSlot(slot, ref entry);
        }

        _readyItems[priorityIndex].Enqueue(item);
        return TryReserveDrainer(maxDrainers, priorityIndex) ? 1 : 0;
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
            for (int i = 0; i < count; i++)
            {
                int slot = slots[i];
                if (slot <= 0 || slot >= _slots.Count)
                {
                    continue;
                }

                Slot entry = _slots[slot];
                if (!entry.InUse)
                {
                    continue;
                }

                TItem item = entry.Item;
                ReleaseSlot(slot, ref entry);
                _readyItems[priorityIndex].Enqueue(item);
                readyCount++;
            }

            int drainerCount = 0;
            while (drainerCount < readyCount && TryReserveDrainer(maxDrainers, priorityIndex))
                drainerCount++;

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
        out int retiredItems,
        out bool hasMoreWork)
    {
        BeginDrain(priorityIndex);
        int claimed = Drain(scheduler, maxItems, priorityIndex, out retiredItems);
        hasMoreWork = FinishDrain(maxDrainers, priorityIndex);
        return claimed;
    }

    private int Drain(Scheduler scheduler, int maxItems, int priorityIndex, out int retiredItems)
    {
        int claimed = 0;
        retiredItems = 0;
        int limit = maxItems <= 0 ? DrainQuantum : maxItems;
        if (!TryTakeReady(out TItem item, out bool hasMoreReady, priorityIndex))
        {
            return 0;
        }

        WorkItemExecutionResult result = scheduler.ExecuteStreamItem(ref item);
        claimed += result.CompletedWorkItems;
        if (result.Retire)
            retiredItems++;
        else
            RequeueReady(item, priorityIndex);

        if (TItem.AllowBatchDrain && hasMoreReady && retiredItems < limit)
        {
            claimed += DrainReadyBatch(
                scheduler,
                limit - retiredItems,
                priorityIndex,
                out int batchRetiredItems);
            retiredItems += batchRetiredItems;
        }

        return claimed;
    }

    private int DrainReadyBatch(
        Scheduler scheduler,
        int limit,
        int priorityIndex,
        out int retiredItems)
    {
        retiredItems = 0;
        TItem[]? items = s_drainBuffer;
        if (items is null || items.Length < limit)
        {
            items = new TItem[Math.Max(limit, DrainQuantum)];
            s_drainBuffer = items;
        }

        int itemCount = TakeReadyBatch(items, limit, priorityIndex);
        int completedWorkItems = 0;
        try
        {
            for (int i = 0; i < itemCount; i++)
            {
                WorkItemExecutionResult result = scheduler.ExecuteStreamItem(ref items[i]);
                completedWorkItems += result.CompletedWorkItems;
                if (result.Retire)
                    retiredItems++;
                else
                    RequeueReady(items[i], priorityIndex);
            }
        }
        finally
        {
            if (ContainsReferences)
            {
                Array.Clear(items, 0, itemCount);
            }
        }

        return completedWorkItems;
    }

    private void RequeueReady(in TItem item, int priorityIndex)
    {
        _readyItems[priorityIndex].Enqueue(item);
    }

    private void BeginDrain(int priorityIndex)
    {
        int remaining = Interlocked.Decrement(ref _scheduledDrainers[priorityIndex]);
        Debug.Assert(remaining >= 0, "Scheduled work-stream drainer count underflowed.");
        if (remaining < 0)
            Interlocked.Exchange(ref _scheduledDrainers[priorityIndex], 0);
    }

    private bool FinishDrain(int maxDrainers, int priorityIndex)
    {
        return TryReserveDrainer(maxDrainers, priorityIndex);
    }

    private bool TryReserveDrainer(int maxDrainers, int priorityIndex)
    {
        int limit = Math.Max(1, maxDrainers);
        while (!_readyItems[priorityIndex].IsEmpty)
        {
            int current = Volatile.Read(ref _scheduledDrainers[priorityIndex]);
            if (current >= limit)
                return false;
            if (Interlocked.CompareExchange(
                    ref _scheduledDrainers[priorityIndex],
                    current + 1,
                    current) == current)
                return true;
        }
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
        bool found = _readyItems[priorityIndex].TryDequeue(out TItem dequeued);
        item = dequeued;
        hasMoreReady = !_readyItems[priorityIndex].IsEmpty;
        return found;
    }

    private int TakeReadyBatch(TItem[] items, int limit, int priorityIndex)
    {
        int claimed = 0;
        while (claimed < limit &&
               _readyItems[priorityIndex].TryDequeue(out TItem item))
        {
            items[claimed++] = item;
        }

        return claimed;
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

    public static bool AllowBatchDrain => true;

    public static WorkItemExecutionResult Execute(ref ScheduledJob<T> item)
    {
        item._job.Execute();
        return new WorkItemExecutionResult(retire: true, completedWorkItems: 1);
    }

    public static int Abandon(ref ScheduledJob<T> item)
    {
        return 1;
    }

    public static void Release(ref ScheduledJob<T> item)
    {
        item = default;
    }
}

internal readonly struct ScheduledParallelTokenSource<T> : IWorkStreamItemSource<ScheduledParallelToken<T>>
    where T : struct, IJobParallelFor
{
    private readonly ParallelJobGroup<T> _group;
    private readonly JobHandle _state;

    internal ScheduledParallelTokenSource(ParallelJobGroup<T> group, JobHandle state)
    {
        _group = group;
        _state = state;
    }

    public ScheduledParallelToken<T> Create(int index)
    {
        _ = index;
        _group.AddTokenReference();
        return new ScheduledParallelToken<T>(_group, _state);
    }
}

internal struct ScheduledParallelToken<T> : IWorkStreamItem<ScheduledParallelToken<T>>
    where T : struct, IJobParallelFor
{
    private ParallelJobGroup<T>? _group;
    private JobHandle _state;

    internal ScheduledParallelToken(ParallelJobGroup<T> group, JobHandle state)
    {
        _group = group;
        _state = state;
    }

    public static JobHandle GetState(in ScheduledParallelToken<T> item)
    {
        return item._state;
    }

    // Re-queue after a bounded claim quantum so newly-arrived higher-priority work can preempt a
    // large low-priority parallel dispatch. Generic WorkStream batching is disabled because a
    // single drainer must not capture every token for the same parallel group.
    public static bool AllowBatchDrain => false;

    public static WorkItemExecutionResult Execute(ref ScheduledParallelToken<T> item)
    {
        ParallelJobGroup<T> group = item._group
            ?? throw new InvalidOperationException("Parallel work token has no group.");
        return group.ExecuteClaimQuantum();
    }

    public static int Abandon(ref ScheduledParallelToken<T> item)
    {
        return item._group?.AbandonUnclaimedBatches() ?? 0;
    }

    public static void Release(ref ScheduledParallelToken<T> item)
    {
        ParallelJobGroup<T>? group = item._group;
        item = default;
        group?.ReleaseReference();
    }
}

internal sealed class ParallelJobGroup<T>
    where T : struct, IJobParallelFor
{
    private const int ClaimQuantum = 16;
    private static readonly ConcurrentStack<ParallelJobGroup<T>> Pool = new();

    private T _job;
    private int _length;
    private int _batchSize;
    private int _batchCount;
    private int _nextBatch;
    private int _references;
    private bool _measureCost;

    private ParallelJobGroup()
    {
    }

    internal static ParallelJobGroup<T> Rent(
        in T job,
        int length,
        int batchSize,
        int batchCount,
        bool measureCost)
    {
        if (!Pool.TryPop(out ParallelJobGroup<T>? group))
            group = new ParallelJobGroup<T>();

        group._job = job;
        group._length = length;
        group._batchSize = batchSize;
        group._batchCount = batchCount;
        group._nextBatch = 0;
        group._references = 1; // scheduling owner; prepared tokens add their own references
        group._measureCost = measureCost;
        return group;
    }

    internal void AddTokenReference()
    {
        Interlocked.Increment(ref _references);
    }

    internal void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _references) != 0)
            return;

        _job = default;
        _length = 0;
        _batchSize = 0;
        _batchCount = 0;
        _nextBatch = 0;
        _measureCost = false;
        Pool.Push(this);
    }

    internal WorkItemExecutionResult ExecuteClaimQuantum()
    {
        int completed = 0;
        int faulted = 0;
        int processedItems = 0;
        ExceptionDispatchInfo? firstFault = null;
        long startedAt = _measureCost ? Stopwatch.GetTimestamp() : 0;

        while (completed < ClaimQuantum)
        {
            int batchIndex = Interlocked.Increment(ref _nextBatch) - 1;
            if (batchIndex >= _batchCount)
                break;

            int start = batchIndex * _batchSize;
            int end = Math.Min(start + _batchSize, _length);
            processedItems += end - start;
            T batchJob = _job; // preserve the previous one-job-copy-per-batch semantics
            try
            {
                for (int index = start; index < end; index++)
                    batchJob.Execute(index);
            }
            catch (Exception exception)
            {
                firstFault ??= ExceptionDispatchInfo.Capture(exception);
                faulted++;
            }

            completed++;
        }

        if (_measureCost && completed > 0)
        {
            ParallelBatchSizer<T>.Record(
                Stopwatch.GetTimestamp() - startedAt,
                processedItems);
        }

        bool exhausted = Volatile.Read(ref _nextBatch) >= _batchCount;
        return new WorkItemExecutionResult(
            retire: exhausted,
            completedWorkItems: completed,
            faulted,
            firstFault);
    }

    internal int AbandonUnclaimedBatches()
    {
        while (true)
        {
            int next = Volatile.Read(ref _nextBatch);
            if (next >= _batchCount)
                return 0;
            if (Interlocked.CompareExchange(ref _nextBatch, _batchCount, next) == next)
                return _batchCount - next;
        }
    }
}


