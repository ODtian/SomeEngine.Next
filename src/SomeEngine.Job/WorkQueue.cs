using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal interface IWorkQueue : IDisposable
{
    void EnqueueSynchronousRanges(SynchronousRangeBatch batch, uint epoch, int count, JobScheduleOptions options);
    void RegisterCategory(JobCategory category);

    void CategoryExecutionFinished();

    void EnsureItemCapacity(int workItemCount);

    void Enqueue(WorkBatch work);

    void EnqueueReadySingle<TItem>(WorkStream<TItem> stream, in TItem item, JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>;

    void EnqueueReadyMany<TItem, TSource>(
        WorkStream<TItem> stream,
        int count,
        int logicalWorkItemCount,
        ref TSource source,
        JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
        where TSource : struct, IWorkStreamItemSource<TItem>;

    long WakeVersion { get; }

    bool TryExecuteOne(bool wait);

    bool WaitAndExecuteOne(long wakeVersion);

    bool TryHandoffLatencyWork(
        object? state,
        Action<object?, int> action,
        int value,
        JobPriority priority,
        out long sequence);

    void JoinLatencyWork(long sequence);

    bool TryReclaimLatencyWork(long sequence);

    void Pulse();
}

internal sealed partial class WorkQueue : IWorkQueue
{
    private const int NoWorkerThread = -1;
    private const int LocalDequeCapacity = 4_096;
    private const int WaitForExternalStateMilliseconds = 10;
    private const int DrainBatchItemLimit = 64;

    private readonly Scheduler _scheduler;
    private readonly JobCounters _counters;
    private readonly int _maxQueuedWorkItems;
    private readonly int _baseSpinCount;
    private readonly int _busySpinCount;
    // Monitor.Wait/Pulse require a monitor object; keep this separate from Lock.
    private readonly object _stateLock = new();
    private readonly object _workerWaitLock = new();
    private readonly AutoResetEvent _latencyCompletedSignal = new(initialState: false);
    private readonly MpmcInjector<ReadyTicket>?[] _injectors;
    private readonly ConcurrentQueue<ReadyTicket>[] _injectorOverflow;
    private readonly ChaseLevDeque<ReadyTicket>?[,] _localQueues;
    private readonly WorkQueueThread[] _workers;
    private int _queuedWorkItemCount;
    private int _readyTicketCount;
    private int _activeDrainerCount;
    private int _sleepingWorkerCount;
    private long _wakeEpoch;
    private object? _latencyState;
    private Action<object?, int>? _latencyAction;
    private SomeEngine.Diagnostics.TraceContext _latencyTrace;
    private ExceptionDispatchInfo? _latencyFailure;
    private int _latencyValue;
    private int _latencyPriorityIndex;
    private long _latencyRequestedSequence;
    private long _latencyClaimedSequence;
    private long _latencyCompletedSequence;
    private long _latencyJoinedSequence;
    private int _disposeState;

    [ThreadStatic]
    private static int s_workerIndexPlusOne;

    [ThreadStatic]
    private static WorkQueue? s_workerQueue;

    [ThreadStatic]
    private static int s_spinBudget;

    [ThreadStatic]
    private static int s_latencySampleCounter;

    internal WorkQueue(Scheduler scheduler, JobConfiguration config, JobCounters counters)
    {
        _scheduler = scheduler;
        _counters = counters;
        _maxQueuedWorkItems = config.MaxQueuedWorkItems;
        _baseSpinCount = config.WorkerSpinCount;
        _busySpinCount = config.BusyWorkerSpinCount;
        _injectors = new MpmcInjector<ReadyTicket>?[JobPriorityOrder.Count];
        _injectorOverflow = new ConcurrentQueue<ReadyTicket>[JobPriorityOrder.Count];
        for (int index = 0; index < _injectors.Length; index++) _injectorOverflow[index] = new();
        _localQueues = new ChaseLevDeque<ReadyTicket>?[config.WorkerCount, JobPriorityOrder.Count];
        _workers = new WorkQueueThread[config.WorkerCount];
        for (int index = 0; index < _workers.Length; index++) _workers[index] = WorkQueueThread.Rent(this, index);
    }

    public void EnqueueSynchronousRanges(SynchronousRangeBatch batch, uint epoch, int count, JobScheduleOptions options)
    {
        ReserveQueuedCapacity(count);
        try
        {
            int worker = CurrentWorkerIndex;
            ReadyTicket ticket = _counters.Enabled
                ? new(new TimedRangeBatch(batch, epoch), SampleReadyTime()) : new(batch, epoch);
            PublishTickets(ticket, count, JobPriorityOrder.PriorityIndex(options.Priority), worker, options.Category);
            _counters.Queued(count, options.Category is null && worker != NoWorkerThread && worker < _workers.Length);
            _counters.QueueHighWater(Volatile.Read(ref _queuedWorkItemCount));
        }
        catch
        {
            ReleaseQueuedCapacity(count);
            throw;
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
        ReserveQueuedCapacity(work.Count);
        try
        {
            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = work.Stream.Category is null && workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = work.PriorityIndex;
            int drainerCount = work.MakeReady(StreamDrainerLimit(work.Stream), priorityIndex);
            PublishDrainers(work.Stream, drainerCount, priorityIndex, workerIndex);
            _counters.Queued(work.Count, useLocalQueue);
            _counters.QueueHighWater(Volatile.Read(ref _queuedWorkItemCount));
        }
        catch
        {
            ReleaseQueuedCapacity(work.Count);
            throw;
        }

        work.ReleaseSlotArray();
    }

    public void EnqueueReadySingle<TItem>(WorkStream<TItem> stream, in TItem item, JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
    {
        ReserveQueuedCapacity(1);
        try
        {
            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = stream.Category is null && workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = JobPriorityOrder.PriorityIndex(priority);
            int drainerCount = stream.PrepareReady(item, StreamDrainerLimit(stream), priorityIndex);
            PublishDrainers(stream, drainerCount, priorityIndex, workerIndex);
            _counters.Queued(1, useLocalQueue);
            _counters.QueueHighWater(Volatile.Read(ref _queuedWorkItemCount));
        }
        catch
        {
            ReleaseQueuedCapacity(1);
            throw;
        }
    }

    public void EnqueueReadyMany<TItem, TSource>(
        WorkStream<TItem> stream,
        int count,
        int logicalWorkItemCount,
        ref TSource source,
        JobPriority priority)
        where TItem : struct, IWorkStreamItem<TItem>
        where TSource : struct, IWorkStreamItemSource<TItem>
    {
        if (logicalWorkItemCount < count)
            throw new ArgumentOutOfRangeException(nameof(logicalWorkItemCount));

        ReserveQueuedCapacity(logicalWorkItemCount);
        try
        {
            int workerIndex = CurrentWorkerIndex;
            bool useLocalQueue = stream.Category is null && workerIndex != NoWorkerThread && workerIndex < _workers.Length;
            int priorityIndex = JobPriorityOrder.PriorityIndex(priority);
            int drainerCount = stream.PrepareManyReady(count, ref source, StreamDrainerLimit(stream), priorityIndex);
            PublishDrainers(stream, drainerCount, priorityIndex, workerIndex);
            _counters.Queued(logicalWorkItemCount, useLocalQueue);
            _counters.QueueHighWater(Volatile.Read(ref _queuedWorkItemCount));
        }
        catch
        {
            ReleaseQueuedCapacity(logicalWorkItemCount);
            throw;
        }
    }

    public long WakeVersion => Volatile.Read(ref _wakeEpoch);

    public bool TryExecuteOne(bool wait)
        => TryExecuteOne(wait, waitIndefinitely: false, wait ? WakeVersion : 0);

    public bool WaitAndExecuteOne(long wakeVersion)
        => TryExecuteOne(wait: true, waitIndefinitely: false, wakeVersion, waitForState: true);

    private bool TryExecuteOne(bool wait, bool waitIndefinitely, long wakeVersion, bool waitForState = false)
    {
        int workerIndex = CurrentWorkerIndex;
        if (!TryDequeueForWorker(workerIndex, out ReadyTicket ticket, out bool stole, out int priorityIndex, out JobCategory? category))
        {
            if (!wait || !WaitForWork(waitIndefinitely, wakeVersion, waitForState)) return false;
            if (!TryDequeueForWorker(workerIndex, out ticket, out stole, out priorityIndex, out category)) return false;
        }
        Interlocked.Decrement(ref _readyTicketCount);
        Interlocked.Increment(ref _activeDrainerCount);
        if (_counters.Enabled)
            _counters.ReadyTicketExecuted(ticket.PublishedAt == 0 ? 0 : Stopwatch.GetTimestamp() - ticket.PublishedAt);
        int claimed = 0;
        bool hasMoreWork = false;
        WorkStream? stream = null;
        bool acquiredCategory = category is not null && !_scheduler.IsExecutingCategory(category);
        JobCategory? previousCategory = _scheduler.SetCurrentCategory(category);
        try
        {
            if (ticket.Data is SynchronousRangeBatch ranges) ranges.Execute(_scheduler, ticket.Epoch, workerIndex);
            else
            {
                stream = (WorkStream)ticket.Data;
                claimed = stream.DrainAndFinish(_scheduler, DrainBatchItemLimit, priorityIndex,
                    StreamDrainerLimit(stream), out _, out hasMoreWork);
            }
        }
        finally
        {
            _scheduler.SetCurrentCategory(previousCategory);
            if (stream is not null) FinishDrain(stream, claimed, hasMoreWork, priorityIndex);
            else
            {
                ReleaseQueuedCapacity(1);
                Interlocked.Decrement(ref _activeDrainerCount);
                if (IsDisposed) WakeWorkers();
            }
            if (acquiredCategory) category!.CompleteExecution();
        }
        if (stole) _counters.Stolen();
        if (workerIndex != NoWorkerThread) s_spinBudget = _busySpinCount;
        return true;
    }

    public bool TryHandoffLatencyWork(
        object? state,
        Action<object?, int> action,
        int value,
        JobPriority priority,
        out long sequence)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_workers.Length == 0)
            {
                sequence = 0;
                return false;
            }

            while (_latencyCompletedSequence != _latencyRequestedSequence ||
                   _latencyJoinedSequence != _latencyRequestedSequence)
            {
                Monitor.Wait(_stateLock);
                ObjectDisposedException.ThrowIf(IsDisposed, this);
            }

            sequence = checked(_latencyRequestedSequence + 1);
            _latencyState = state;
            _latencyAction = action;
            _latencyTrace = SomeEngine.Diagnostics.EngineTrace.Context;
            _latencyFailure = null;
            _latencyValue = value;
            _latencyPriorityIndex = JobPriorityOrder.PriorityIndex(priority);
            Volatile.Write(ref _latencyRequestedSequence, sequence);
            Interlocked.Increment(ref _wakeEpoch);
            WakeWorkerThreads(1);
            return true;
        }
    }

    public void JoinLatencyWork(long sequence)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(JobTrace.JoinLatency);
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        while (Volatile.Read(ref _latencyCompletedSequence) != sequence)
        {
            _latencyCompletedSignal.WaitOne();
        }

        ExceptionDispatchInfo? failure;
        lock (_stateLock)
        {
            if (_latencyCompletedSequence != sequence ||
                _latencyJoinedSequence >= sequence)
            {
                throw new InvalidOperationException("Latency work has already been joined or superseded.");
            }

            failure = _latencyFailure;
            _latencyFailure = null;
            _latencyJoinedSequence = sequence;
            Monitor.PulseAll(_stateLock);
        }
        failure?.Throw();
    }

    public bool TryReclaimLatencyWork(long sequence)
    {
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_latencyRequestedSequence != sequence ||
                _latencyJoinedSequence >= sequence)
            {
                throw new InvalidOperationException(
                    "Latency work has already been joined or superseded.");
            }
            if (_latencyClaimedSequence == sequence)
                return false;

            _latencyClaimedSequence = sequence;
            _latencyCompletedSequence = sequence;
            _latencyJoinedSequence = sequence;
            _latencyState = null;
            _latencyAction = null;
            _latencyFailure = null;
            Monitor.PulseAll(_stateLock);
            return true;
        }
    }

    private void FinishDrain(
        WorkStream stream,
        int claimed,
        bool hasMoreWork,
        int priorityIndex)
    {
        if (claimed > 0)
            ReleaseQueuedCapacity(claimed);

        if (hasMoreWork)
            PublishDrainers(stream, 1, priorityIndex, CurrentWorkerIndex);

        Interlocked.Decrement(ref _activeDrainerCount);
        if (IsDisposed)
            WakeWorkers();
    }

    public void Pulse()
    {
        // A completion changes the condition awaited by helpers, including
        // workers joining child jobs. It does not create new executable work.
        Interlocked.Increment(ref _wakeEpoch);
        lock (_stateLock) Monitor.PulseAll(_stateLock);
    }

    public void Dispose()
    {
        // The public scheduler drains submitted work before releasing wait handles.
        while (Volatile.Read(ref _queuedWorkItemCount) != 0 || Volatile.Read(ref _activeDrainerCount) != 0)
        {
            if (!TryExecuteOne(wait: false)) Thread.Yield();
        }
        lock (_stateLock)
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
                return;
            Interlocked.Increment(ref _wakeEpoch);
            Monitor.PulseAll(_stateLock);
        }

        WakeWorkerThreads(int.MaxValue);
        foreach (WorkQueueThread worker in _workers) worker.Return();
        _latencyCompletedSignal.Dispose();
    }

    private void ReserveQueuedCapacity(int workItemCount)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            int current = Volatile.Read(ref _queuedWorkItemCount);
            if (_maxQueuedWorkItems - current < workItemCount)
                ThrowCapacityExhausted();
            if (Interlocked.CompareExchange(
                    ref _queuedWorkItemCount,
                    current + workItemCount,
                    current) == current)
                return;
        }
    }

    private void ReleaseQueuedCapacity(int workItemCount)
    {
        if (workItemCount <= 0)
            return;

        int remaining = Interlocked.Add(ref _queuedWorkItemCount, -workItemCount);
        Debug.Assert(remaining >= 0, "Queued logical work count underflowed.");
        if (remaining < 0)
            Interlocked.Exchange(ref _queuedWorkItemCount, 0);
    }

    private void ThrowCapacityExhausted()
    {
        throw new InvalidOperationException(
            $"Job queue capacity exhausted ({_maxQueuedWorkItems}).");
    }

    private void PublishDrainers(WorkStream stream, int count, int priorityIndex, int workerIndex)
    {
        if (count <= 0) return;
        PublishTickets(new ReadyTicket(stream, SampleReadyTime()), count, priorityIndex, workerIndex, stream.Category);
    }

    private long SampleReadyTime() =>
        _counters.Enabled && ((++s_latencySampleCounter & 15) == 1) ? Stopwatch.GetTimestamp() : 0;

    private void PublishTickets(in ReadyTicket ticket, int count, int priorityIndex, int workerIndex, JobCategory? category)
    {
        Interlocked.Add(ref _readyTicketCount, count);
        _counters.ReadyTicketsPublished(count);
        if (category is not null)
        {
            ConcurrentQueue<ReadyTicket> queue = _categoryQueues[category].Priorities[priorityIndex];
            for (int index = 0; index < count; index++) queue.Enqueue(ticket);
            WakeWorkers(count);
            return;
        }
        bool local = workerIndex != NoWorkerThread && workerIndex < _workers.Length;
        ChaseLevDeque<ReadyTicket>? deque = local
            ? Volatile.Read(ref _localQueues[workerIndex, priorityIndex]) ?? CreateLocalQueue(workerIndex, priorityIndex) : null;
        MpmcInjector<ReadyTicket>? injector = local
            ? null : Volatile.Read(ref _injectors[priorityIndex]) ?? CreateInjector(priorityIndex);
        for (int index = 0; index < count; index++)
        {
            if (deque is not null && deque.TryPush(ticket)) continue;
            injector ??= Volatile.Read(ref _injectors[priorityIndex]) ?? CreateInjector(priorityIndex);
            if (!injector.TryEnqueue(ticket)) _injectorOverflow[priorityIndex].Enqueue(ticket);
        }
        WakeWorkers(count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private MpmcInjector<ReadyTicket> CreateInjector(int priority)
    {
        var injector = new MpmcInjector<ReadyTicket>(Math.Clamp(_maxQueuedWorkItems, 64, 65_536));
        return Interlocked.CompareExchange(ref _injectors[priority], injector, null) ?? injector;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ChaseLevDeque<ReadyTicket> CreateLocalQueue(int worker, int priority)
    {
        var queue = new ChaseLevDeque<ReadyTicket>(Math.Min(LocalDequeCapacity, _maxQueuedWorkItems));
        return Interlocked.CompareExchange(ref _localQueues[worker, priority], queue, null) ?? queue;
    }

    private bool WaitForWork(bool waitIndefinitely, long observedEpoch, bool waitForState)
    {
        bool workerThread = CurrentWorkerIndex != NoWorkerThread && !waitForState;
        int spinCount = workerThread ? Math.Max(_baseSpinCount, s_spinBudget) : 0;
        for (int i = 0; i < spinCount; i++)
        {
            if (HasExecutableTickets())
                return true;
            Thread.SpinWait(4);
        }
        if (workerThread)
        {
            if (spinCount > 0)
                _counters.WorkerSpun();
            s_spinBudget = Math.Max(_baseSpinCount, spinCount / 2);
        }

        object waitLock = workerThread ? _workerWaitLock : _stateLock;
        lock (waitLock)
        {
            if (HasExecutableTickets() ||
                Volatile.Read(ref _latencyClaimedSequence) != Volatile.Read(ref _latencyRequestedSequence))
                return true;
            if (IsDisposed) return false;

            if (workerThread) Interlocked.Increment(ref _sleepingWorkerCount);
            try
            {
                if (Volatile.Read(ref _wakeEpoch) != observedEpoch ||
                    HasExecutableTickets() ||
                    Volatile.Read(ref _latencyClaimedSequence) != Volatile.Read(ref _latencyRequestedSequence))
                    return true;
                if (workerThread) _counters.WorkerParked();
                if (waitIndefinitely) Monitor.Wait(waitLock);
                else Monitor.Wait(waitLock, WaitForExternalStateMilliseconds);
                if (workerThread) _counters.WorkerWoke();
                return true;
            }
            finally
            {
                if (workerThread) Interlocked.Decrement(ref _sleepingWorkerCount);
            }
        }
    }

    private void WakeWorkers(int maximumWorkerCount = int.MaxValue)
    {
        Interlocked.Increment(ref _wakeEpoch);
        WakeWorkerThreads(maximumWorkerCount);
        // External helpers and latency producers wait for different state from workers.
        lock (_stateLock) Monitor.PulseAll(_stateLock);
    }

    private void WakeWorkerThreads(int maximumCount)
    {
        if (Volatile.Read(ref _sleepingWorkerCount) == 0) return;
        lock (_workerWaitLock)
        {
            int count = Math.Min(maximumCount, _sleepingWorkerCount);
            for (int worker = 0; worker < count; worker++) Monitor.Pulse(_workerWaitLock);
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    private bool TryDequeueForWorker(int workerIndex, out ReadyTicket ticket,
        out bool stole, out int priorityIndex, out JobCategory? category)
    {
        category = null;
        return Volatile.Read(ref _categoryCount) == 0
            ? TryDequeueDefault(workerIndex, out ticket, out stole, out priorityIndex)
            : TryDequeueByCategory(workerIndex, out ticket, out stole, out priorityIndex, out category);
    }

    private bool TryDequeueDefault(int workerIndex, out ReadyTicket ticket, out bool stole, out int priorityIndex)
    {
        ticket = default;
        stole = false;
        priorityIndex = JobPriorityOrder.PriorityIndex(JobPriority.Normal);
        if (Volatile.Read(ref _readyTicketCount) == 0) return false;
        for (int priority = 0; priority < JobPriorityOrder.Count; priority++)
        {
            if (workerIndex != NoWorkerThread &&
                Volatile.Read(ref _localQueues[workerIndex, priority]) is { } local && local.TryPop(out ticket))
            {
                priorityIndex = priority;
                return true;
            }
            if (Volatile.Read(ref _injectors[priority]) is { } injector && injector.TryDequeue(out ticket) ||
                _injectorOverflow[priority].TryDequeue(out ticket))
            {
                priorityIndex = priority;
                return true;
            }
            for (int worker = 0; worker < _workers.Length; worker++)
            {
                if (worker == workerIndex) continue;
                if (Volatile.Read(ref _localQueues[worker, priority]) is { } victim && victim.TrySteal(out ticket))
                {
                    stole = workerIndex != NoWorkerThread;
                    priorityIndex = priority;
                    return true;
                }
            }
        }
        return false;
    }

    internal void WorkerLoop(int workerIndex)
    {
        s_workerQueue = this;
        s_workerIndexPlusOne = workerIndex + 1;
        s_spinBudget = _baseSpinCount;
        try
        {
            while (true)
            {
                if (TryExecuteLatencyWork())
                {
                    continue;
                }

                if (IsDisposed &&
                    Volatile.Read(ref _queuedWorkItemCount) == 0 &&
                    Volatile.Read(ref _readyTicketCount) == 0 &&
                    Volatile.Read(ref _activeDrainerCount) == 0 &&
                    Volatile.Read(ref _latencyCompletedSequence) ==
                        Volatile.Read(ref _latencyRequestedSequence))
                {
                    return;
                }

                TryExecuteOne(wait: true, waitIndefinitely: true, WakeVersion);
            }
        }
        finally
        {
            s_workerIndexPlusOne = 0;
            s_workerQueue = null;
            s_spinBudget = 0;
        }
    }

    private bool TryExecuteLatencyWork()
    {
        SomeEngine.Diagnostics.TraceContext traceContext;
        object? state;
        Action<object?, int>? action;
        int value;
        long sequence;
        lock (_stateLock)
        {
            sequence = _latencyRequestedSequence;
            if (sequence == _latencyClaimedSequence)
            {
                return false;
            }
            if (HasQueuedHigherPriority(_latencyPriorityIndex))
            {
                return false;
            }

            _latencyClaimedSequence = sequence;
            state = _latencyState;
            action = _latencyAction;
            traceContext = _latencyTrace;
            value = _latencyValue;
        }

        ExceptionDispatchInfo? failure = null;
        try
        {
            using var association = SomeEngine.Diagnostics.EngineTrace.Associate(traceContext);
            using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(JobTrace.Latency);
            action!(state, value);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }

        lock (_stateLock)
        {
            _latencyFailure = failure;
            _latencyState = null;
            _latencyAction = null;
            Volatile.Write(ref _latencyCompletedSequence, sequence);
            _latencyCompletedSignal.Set();
            Monitor.PulseAll(_stateLock);
        }
        return true;
    }

    private bool HasQueuedHigherPriority(int priorityIndex)
    {
        for (int priority = 0; priority < priorityIndex; priority++)
        {
            if (Volatile.Read(ref _injectors[priority]) is { IsEmpty: false } || !_injectorOverflow[priority].IsEmpty)
            {
                return true;
            }
            for (int worker = 0; worker < _workers.Length; worker++)
            {
                if (Volatile.Read(ref _localQueues[worker, priority]) is { IsEmpty: false })
                {
                    return true;
                }
            }
        }
        return false;
    }

    private int CurrentWorkerIndex =>
        ReferenceEquals(s_workerQueue, this) && s_workerIndexPlusOne != 0
            ? s_workerIndexPlusOne - 1
            : NoWorkerThread;

    private int MaxStreamDrainers => Math.Max(1, _workers.Length + 1);

    private int StreamDrainerLimit(WorkStream stream) => stream.Category is { } category
        ? Math.Min(MaxStreamDrainers, category.Config.MaxExecuting) : MaxStreamDrainers;

    private readonly struct ReadyTicket(object data, long metadata)
    {
        internal object Data { get; } = data;
        internal long PublishedAt => metadata;
        internal uint Epoch => (uint)metadata;
    }

    private sealed unsafe class TimedRangeBatch : SynchronousRangeBatch
    {
        private readonly SynchronousRangeBatch _batch;
        private readonly uint _epoch;

        internal TimedRangeBatch(SynchronousRangeBatch batch, uint epoch) : base(&ExecuteTimedBatch)
        {
            _batch = batch;
            _epoch = epoch;
        }

        private static void ExecuteTimedBatch(SynchronousRangeBatch batch, Scheduler scheduler, uint _, int workerIndex)
        {
            var timed = (TimedRangeBatch)batch;
            timed._batch.Execute(scheduler, timed._epoch, workerIndex);
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
    private readonly int _preparedCount;
    private readonly bool _pooledSlotsArray;
    private readonly int _priorityIndex;

    private WorkBatch(
        WorkStream stream,
        int singleSlot,
        int[]? slots,
        int preparedCount,
        int logicalCount,
        bool pooledSlotsArray,
        int priorityIndex)
    {
        _stream = stream;
        _singleSlot = singleSlot;
        _slots = slots;
        _preparedCount = preparedCount;
        Count = logicalCount;
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
            preparedCount: 1,
            logicalCount: 1,
            pooledSlotsArray: false,
            JobPriorityOrder.PriorityIndex(priority));
    }

    internal static WorkBatch CreateArray(
        WorkStream stream,
        int[] slots,
        int preparedCount,
        int logicalCount,
        bool pooledSlotsArray,
        JobPriority priority)
    {
        return new WorkBatch(
            stream,
            singleSlot: 0,
            slots,
            preparedCount,
            logicalCount,
            pooledSlotsArray,
            JobPriorityOrder.PriorityIndex(priority));
    }

    internal int MakeReady(int maxDrainers, int priorityIndex)
    {
        WorkStream stream = Stream;
        if (_singleSlot != 0)
        {
            return stream.MakeReady(_singleSlot, maxDrainers, priorityIndex);
        }

        int[] slots = _slots ?? throw new InvalidOperationException(MissingSlotsMessage);
        return stream.MakeReady(slots, _preparedCount, maxDrainers, priorityIndex);
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

        for (int i = 0; i < _preparedCount; i++)
        {
            _stream.Cancel(_slots[i]);
        }

        ReleaseSlotArray();
    }
}



