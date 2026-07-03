using System.Buffers;

namespace SomeEngine.Job;

internal interface IWorkQueue : IDisposable
{
    void EnsureItemCapacity(int workItemCount);

    void Enqueue(WorkBatch work);

    void EnqueueReadySingle<TItem>(WorkStream<TItem> stream, in TItem item, JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>;

    void EnqueueReadyMany<TItem, TSource>(
        WorkStream<TItem> stream,
        int count,
        ref TSource source,
        JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
        where TSource : struct, IWorkStreamItemSource<TItem>;

    bool TryExecuteOne(bool wait);

    void Pulse();
}

internal sealed class WorkQueue : IWorkQueue
{
    private const int NoWorkerThread = -1;
    private const int WorkStealingQueueInitialCapacity = 4;
    private const int WorkStealingQueueGrowthFactor = 2;
    private const int WaitForWorkMilliseconds = 10;
    private const int DrainBatchItemLimit = 64;

    private readonly Scheduler _scheduler;
    private readonly RuntimeCounters _counters;
    private readonly int _maxQueuedWorkItems;
    // Monitor.Wait/Pulse require a monitor object; keep this separate from Lock.
    private readonly object _queueLock = new();
    private readonly Queue<WorkStream>[] _globalQueues;
    private readonly WorkStealingQueue[,] _localQueues;
    private readonly Thread[] _workers;
    private int _queuedWorkItemCount;
    private bool _disposed;

    [ThreadStatic]
    private static int s_workerIndexPlusOne;

    internal WorkQueue(Scheduler scheduler, JobRuntimeConfig config, RuntimeCounters counters)
    {
        _scheduler = scheduler;
        _counters = counters;
        _maxQueuedWorkItems = config.MaxQueuedWorkItems;
        _globalQueues = new Queue<WorkStream>[JobPriorityOrder.Count];
        for (int i = 0; i < _globalQueues.Length; i++)
        {
            _globalQueues[i] = new Queue<WorkStream>();
        }

        _localQueues = new WorkStealingQueue[config.WorkerCount, JobPriorityOrder.Count];
        for (int worker = 0; worker < config.WorkerCount; worker++)
        {
            for (int priority = 0; priority < JobPriorityOrder.Count; priority++)
            {
                _localQueues[worker, priority] = new WorkStealingQueue();
            }
        }

        _workers = new Thread[config.WorkerCount];
        for (int i = 0; i < _workers.Length; i++)
        {
            int workerIndex = i;
            Thread worker = new(() => WorkerLoop(workerIndex))
            {
                IsBackground = true,
                Name = $"SomeEngine.Job Worker {i}"
            };
            _workers[i] = worker;
            worker.Start();
        }
    }

    public void EnsureItemCapacity(int workItemCount)
    {
        if (workItemCount > _maxQueuedWorkItems)
        {
            ThrowCapacityExhausted();
        }
    }

    public void Enqueue(WorkBatch work)
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureQueuedCapacity(work.Count);

            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = work.PriorityIndex;
            int drainerCount = work.MakeReady(MaxStreamDrainers, priorityIndex);
            for (int i = 0; i < drainerCount; i++)
            {
                if (useLocalQueue)
                {
                    _localQueues[workerIndex, priorityIndex].Push(work.Stream);
                }
                else
                {
                    _globalQueues[priorityIndex].Enqueue(work.Stream);
                }
            }

            _queuedWorkItemCount += work.Count;
            _counters.Queued(work.Count, useLocalQueue);
            _counters.QueueHighWater(_queuedWorkItemCount);
            Monitor.PulseAll(_queueLock);
        }

        work.ReleaseSlotArray();
    }

    public void EnqueueReadySingle<TItem>(WorkStream<TItem> stream, in TItem item, JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureQueuedCapacity(1);

            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = JobPriorityOrder.PriorityIndex(priority);
            int drainerCount = stream.PrepareReady(item, MaxStreamDrainers, priorityIndex);
            for (int i = 0; i < drainerCount; i++)
            {
                if (useLocalQueue)
                {
                    _localQueues[workerIndex, priorityIndex].Push(stream);
                }
                else
                {
                    _globalQueues[priorityIndex].Enqueue(stream);
                }
            }

            _queuedWorkItemCount++;
            _counters.Queued(1, useLocalQueue);
            _counters.QueueHighWater(_queuedWorkItemCount);
            Monitor.PulseAll(_queueLock);
        }
    }

    public void EnqueueReadyMany<TItem, TSource>(
        WorkStream<TItem> stream,
        int count,
        ref TSource source,
        JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
        where TSource : struct, IWorkStreamItemSource<TItem>
    {
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureQueuedCapacity(count);

            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = JobPriorityOrder.PriorityIndex(priority);
            int drainerCount = stream.PrepareManyReady(count, ref source, MaxStreamDrainers, priorityIndex);
            for (int i = 0; i < drainerCount; i++)
            {
                if (useLocalQueue)
                {
                    _localQueues[workerIndex, priorityIndex].Push(stream);
                }
                else
                {
                    _globalQueues[priorityIndex].Enqueue(stream);
                }
            }

            _queuedWorkItemCount += count;
            _counters.Queued(count, useLocalQueue);
            _counters.QueueHighWater(_queuedWorkItemCount);
            Monitor.PulseAll(_queueLock);
        }
    }

    public bool TryExecuteOne(bool wait)
    {
        WorkStream stream;
        bool stole = false;
        int priorityIndex = JobPriorityOrder.PriorityIndex(JobPriority.Normal);
        lock (_queueLock)
        {
            int workerIndex = CurrentWorkerIndex;
            if (!TryDequeueForWorker(workerIndex, out stream, out stole, out priorityIndex))
            {
                if (_disposed || !wait)
                {
                    return false;
                }

                Monitor.Wait(_queueLock, TimeSpan.FromMilliseconds(WaitForWorkMilliseconds));
                if (!TryDequeueForWorker(workerIndex, out stream, out stole, out priorityIndex))
                {
                    return false;
                }
            }
        }

        int claimed = stream.DrainAndFinish(
            _scheduler,
            maxItems: DrainBatchItemLimit,
            priorityIndex,
            maxDrainers: MaxStreamDrainers,
            out bool hasMoreWork);

        FinishDrain(stream, claimed, hasMoreWork, priorityIndex);

        if (stole)
        {
            _counters.Stolen();
        }

        return claimed > 0 || hasMoreWork;
    }

    private void FinishDrain(
        WorkStream stream,
        int claimed,
        bool hasMoreWork,
        int priorityIndex)
    {
        lock (_queueLock)
        {
            _queuedWorkItemCount -= claimed;
            if (_queuedWorkItemCount < 0)
                _queuedWorkItemCount = 0;

            if (!hasMoreWork)
                return;

            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            if (useLocalQueue)
                _localQueues[workerIndex, priorityIndex].Push(stream);
            else
                _globalQueues[priorityIndex].Enqueue(stream);

            Monitor.PulseAll(_queueLock);
        }
    }

    public void Pulse()
    {
        lock (_queueLock)
        {
            Monitor.PulseAll(_queueLock);
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Monitor.PulseAll(_queueLock);
        }

        foreach (Thread worker in _workers)
        {
            if (worker.IsAlive)
            {
                worker.Join();
            }
        }
    }

    private void EnsureQueuedCapacity(int workItemCount)
    {
        if (_maxQueuedWorkItems - _queuedWorkItemCount < workItemCount)
        {
            ThrowCapacityExhausted();
        }
    }

    private void ThrowCapacityExhausted()
    {
        throw new InvalidOperationException(
            $"Job queue capacity exhausted ({_maxQueuedWorkItems}).");
    }

    private bool TryDequeueForWorker(
        int workerIndex,
        out WorkStream stream,
        out bool stole,
        out int priorityIndex)
    {
        stream = null!;
        stole = false;
        priorityIndex = JobPriorityOrder.PriorityIndex(JobPriority.Normal);

        for (int priority = 0; priority < JobPriorityOrder.Count; priority++)
        {
            if (workerIndex != NoWorkerThread && _localQueues[workerIndex, priority].TryPop(out stream))
            {
                priorityIndex = priority;
                return true;
            }

            if (_globalQueues[priority].Count > 0)
            {
                stream = _globalQueues[priority].Dequeue();
                priorityIndex = priority;
                return true;
            }

            for (int i = 0; i < _workers.Length; i++)
            {
                if (i == workerIndex)
                {
                    continue;
                }

                if (_localQueues[i, priority].TrySteal(out stream))
                {
                    stole = workerIndex != NoWorkerThread;
                    priorityIndex = priority;
                    return true;
                }
            }
        }

        return false;
    }

    private void WorkerLoop(int workerIndex)
    {
        s_workerIndexPlusOne = workerIndex + 1;
        try
        {
            while (true)
            {
                lock (_queueLock)
                {
                    if (_disposed && _queuedWorkItemCount == 0)
                    {
                        return;
                    }
                }

                TryExecuteOne(wait: true);
            }
        }
        finally
        {
            s_workerIndexPlusOne = 0;
        }
    }

    private static int CurrentWorkerIndex => s_workerIndexPlusOne == 0 ? NoWorkerThread : s_workerIndexPlusOne - 1;

    private int MaxStreamDrainers => Math.Max(1, _workers.Length + 1);

    private sealed class WorkStealingQueue
    {
        private WorkStream?[] _items = new WorkStream?[WorkStealingQueueInitialCapacity];
        private int _head;
        private int _count;

        internal void Push(WorkStream stream)
        {
            if (_count == _items.Length)
            {
                WorkStream?[] expanded = new WorkStream?[_items.Length * WorkStealingQueueGrowthFactor];
                for (int i = 0; i < _count; i++)
                {
                    expanded[i] = _items[(_head + i) % _items.Length];
                }

                _items = expanded;
                _head = 0;
            }

            int tail = (_head + _count) % _items.Length;
            _items[tail] = stream;
            _count++;
        }

        internal bool TryPop(out WorkStream stream)
        {
            if (_count == 0)
            {
                stream = null!;
                return false;
            }

            int tail = (_head + _count - 1) % _items.Length;
            stream = _items[tail]!;
            _items[tail] = null;
            _count--;
            if (_count == 0)
            {
                _head = 0;
            }

            return true;
        }

        internal bool TrySteal(out WorkStream stream)
        {
            if (_count == 0)
            {
                stream = null!;
                return false;
            }

            stream = _items[_head]!;
            _items[_head] = null;
            _head = (_head + 1) % _items.Length;
            _count--;
            if (_count == 0)
            {
                _head = 0;
            }

            return true;
        }
    }
}

internal readonly struct WorkBatch
{
    private const string MissingStreamMessage = "Work batch has no stream.";
    private const string MissingSlotsMessage = "Work batch has no slots.";
    private readonly WorkStream? _stream;
    private readonly int _singleSlot;
    private readonly int[]? _slots;
    private readonly bool _pooledSlotsArray;
    private readonly int _priorityIndex;

    private WorkBatch(
        WorkStream stream,
        int singleSlot,
        int[]? slots,
        int count,
        bool pooledSlotsArray,
        int priorityIndex)
    {
        _stream = stream;
        _singleSlot = singleSlot;
        _slots = slots;
        Count = count;
        _pooledSlotsArray = pooledSlotsArray;
        _priorityIndex = priorityIndex;
    }

    internal int Count { get; }

    internal int PriorityIndex => _stream is null
        ? JobPriorityOrder.PriorityIndex(JobPriority.Normal)
        : _priorityIndex;

    internal WorkStream Stream =>
        _stream ?? throw new InvalidOperationException(MissingStreamMessage);

    internal bool HasValue => _stream is not null;

    internal static WorkBatch CreateSingle(WorkStream stream, int slot, JobPriority priority)
    {
        return new WorkBatch(
            stream,
            slot,
            slots: null,
            count: 1,
            pooledSlotsArray: false,
            JobPriorityOrder.PriorityIndex(priority));
    }

    internal static WorkBatch CreateArray(
        WorkStream stream,
        int[] slots,
        int count,
        bool pooledSlotsArray,
        JobPriority priority)
    {
        return new WorkBatch(
            stream,
            singleSlot: 0,
            slots,
            count,
            pooledSlotsArray,
            JobPriorityOrder.PriorityIndex(priority));
    }

    internal int MakeReady(int maxDrainers, int priorityIndex)
    {
        WorkStream stream = Stream;
        if (_singleSlot != 0)
        {
            return stream.MakeReady(_singleSlot, maxDrainers, priorityIndex) ? 1 : 0;
        }

        int[] slots = _slots ?? throw new InvalidOperationException(MissingSlotsMessage);
        return stream.MakeReady(slots, Count, maxDrainers, priorityIndex);
    }

    internal void ReleaseSlotArray()
    {
        if (_slots is null || !_pooledSlotsArray)
        {
            return;
        }

        ArrayPool<int>.Shared.Return(_slots);
    }

    internal void ReleaseJobs()
    {
        if (_stream is null)
        {
            return;
        }

        if (_singleSlot != 0)
        {
            _stream.Cancel(_singleSlot);
            return;
        }

        if (_slots is null)
        {
            return;
        }

        for (int i = 0; i < Count; i++)
        {
            _stream.Cancel(_slots[i]);
        }

        ReleaseSlotArray();
    }
}




