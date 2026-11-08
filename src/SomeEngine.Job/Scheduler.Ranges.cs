using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    /// <summary>Executes the first count items on this scheduler and the calling thread, sharing the current access scope.</summary>
    public void ExecuteRanges<TWork, TItem>(TItem[] items, int count, in TWork work)
        where TWork : struct, IRangeWork<TItem> =>
        ExecuteRanges(items, count, work, GetCurrentScope());

    /// <summary>Uses the supplied resource grant for every item and returns after all executing participants exit.</summary>
    public void ExecuteRanges<TWork, TItem>(TItem[] items, int count, in TWork work,
        JobHandle access, JobScheduleOptions options = default)
        where TWork : struct, IRangeWork<TItem>
    {
        using Submission submission = EnterSubmission();
        ValidatePriority(options);
        EnsureCurrentScopeBelongsToThisRuntime();
        JobPayloadLane lane = JobTraits.GetPayloadLane<TWork>();
        ApplyManagedPayloadPolicy<TWork>(lane);
        if (count == 0) return;
        options = ResolveCategoryOptions(options);
        using CategoryRequest category = EnterSynchronousCategoryRequest(options.Category);
        ExecuteRangesCore(items, count, work, access, options, lane);
    }

    private void ExecuteRangesCore<TWork, TItem>(TItem[] items, int count, in TWork work,
        JobHandle access, JobScheduleOptions options, JobPayloadLane lane)
        where TWork : struct, IRangeWork<TItem>
    {
        ValidateDependency(access);
        if (count <= 32) ExecuteRangeBatch<TWork, TItem, MaskRangeClaims>(items, count, work, access, options, lane);
        else ExecuteRangeBatch<TWork, TItem, SequentialRangeClaims>(items, count, work, access, options, lane);
    }

    private void ExecuteRangeBatch<TWork, TItem, TClaims>(TItem[] items, int count, in TWork work,
        JobHandle access, JobScheduleOptions options, JobPayloadLane lane)
        where TWork : struct, IRangeWork<TItem> where TClaims : struct, IRangeClaims
    {
        bool changeConcurrency = access.Index != 0 && count > 1;
        bool previousConcurrency = changeConcurrency && _completion.SetConcurrentExecution(access, true);
        SynchronousRanges<TWork, TItem, TClaims> batch = SynchronousRanges<TWork, TItem, TClaims>.Rent();
        uint epoch = batch.Start(items, count, work, access);
        ExceptionDispatchInfo? error;
        int faults;
        try
        {
            int helpers = Math.Min(_config.WorkerCount, count - 1);
            if (options.Category is { } category) helpers = Math.Min(helpers, category.Config.MaxExecuting - 1);
            if (helpers > 0) _workQueue.EnqueueSynchronousRanges(batch, epoch, helpers, options);
            _counters.Scheduled(lane);
        }
        finally
        {
            using (EnterCategoryExecution(options.Category)) batch.DrainCaller(this, epoch);
            SpinWait spin = new();
            while (Volatile.Read(ref batch.Remaining) != 0 || Volatile.Read(ref batch.Participants) != 0)
            {
                long wake = _workQueue.WakeVersion;
                if (Volatile.Read(ref batch.Remaining) == 0 && Volatile.Read(ref batch.Participants) == 0) break;
                if (_workQueue.TryExecuteOne(wait: false)) spin.Reset();
                else if (!spin.NextSpinWillYield) spin.SpinOnce();
                else _workQueue.WaitAndExecuteOne(wake);
            }
            error = batch.Error;
            faults = batch.Faults;
            batch.Return();
            if (changeConcurrency) _completion.SetConcurrentExecution(access, previousConcurrency);
        }
        _counters.Executed(count);
        _counters.Faulted(faults);
        error?.Throw();
    }

    private interface IRangeClaims
    {
        static abstract bool UsesWorkerIndex { get; }
        static abstract bool TrySelect(long current, int count, int preferred, out int index, out long next);
    }

    private readonly struct SequentialRangeClaims : IRangeClaims
    {
        public static bool UsesWorkerIndex => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySelect(long current, int count, int preferred, out int index, out long next)
        {
            index = (int)(uint)current;
            next = current + 1;
            return index < count;
        }
    }

    private readonly struct MaskRangeClaims : IRangeClaims
    {
        public static bool UsesWorkerIndex => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySelect(long current, int count, int preferred, out int index, out long next)
        {
            uint available = ~(uint)current & (uint.MaxValue >> (32 - count));
            if (available == 0) { index = 0; next = current; return false; }
            uint preferredBit = (uint)preferred < (uint)count ? 1u << preferred : 0;
            uint selected = (available & preferredBit) != 0 ? preferredBit : available & unchecked(0u - available);
            index = BitOperations.TrailingZeroCount(selected);
            next = current | selected;
            return true;
        }
    }

    private sealed unsafe class SynchronousRanges<TWork, TItem, TClaims> : SynchronousRangeBatch
        where TWork : struct, IRangeWork<TItem> where TClaims : struct, IRangeClaims
    {
        [ThreadStatic] private static SynchronousRanges<TWork, TItem, TClaims>[]? s_free;
        [ThreadStatic] private static int s_freeCount;

        private TWork _work;
        private TItem[]? _items;
        private int _count;
        private JobHandle _access;
        private long _epochClaims;
        internal int Remaining;
        internal int Participants;
        internal int Faults;
        internal ExceptionDispatchInfo? Error;

        private SynchronousRanges() : base(&ExecuteBatch) { }

        private static void ExecuteBatch(SynchronousRangeBatch batch, Scheduler scheduler, uint epoch, int workerIndex)
            => ((SynchronousRanges<TWork, TItem, TClaims>)batch).Drain(scheduler, epoch, workerIndex);

        internal static SynchronousRanges<TWork, TItem, TClaims> Rent()
        {
            if (s_freeCount == 0)
                return new SynchronousRanges<TWork, TItem, TClaims>();
            int index = --s_freeCount;
            SynchronousRanges<TWork, TItem, TClaims> batch = s_free![index];
            s_free[index] = null!;
            return batch;
        }

        internal uint Start(TItem[] items, int count, in TWork work, JobHandle access)
        {
            _work = work;
            _items = items;
            _count = count;
            Remaining = count;
            Participants = 0;
            Faults = 0;
            Error = null;
            _access = access;
            uint epoch = unchecked((uint)((ulong)_epochClaims >> 32) + 1);
            Interlocked.Exchange(ref _epochClaims, (long)(((ulong)epoch << 32) | 1));
            return epoch;
        }

        internal void Return()
        {
            uint epoch = unchecked((uint)((ulong)_epochClaims >> 32) + 1);
            Interlocked.Exchange(ref _epochClaims, (long)((ulong)epoch << 32));
            _work = default;
            _items = null;
            Error = null;
            _access = default;
            SynchronousRanges<TWork, TItem, TClaims>[] free = s_free ??= new SynchronousRanges<TWork, TItem, TClaims>[4];
            if (s_freeCount == free.Length)
            {
                Array.Resize(ref free, free.Length * 2);
                s_free = free;
            }
            free[s_freeCount++] = this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryClaim(uint epoch, int preferred, out int index)
        {
            index = 0;
            while (true)
            {
                long current = Volatile.Read(ref _epochClaims);
                if ((uint)((ulong)current >> 32) != epoch
                    || !TClaims.TrySelect(current, _count, preferred, out index, out long next)) return false;
                if (Interlocked.CompareExchange(ref _epochClaims, next, current) == current)
                    return true;
            }
        }

        internal void DrainCaller(Scheduler scheduler, uint epoch) => DrainClaimed(scheduler, epoch, 0);

        internal void Drain(Scheduler scheduler, uint epoch, int workerIndex)
        {
            int preferred = TClaims.UsesWorkerIndex ? workerIndex + 1 : -1;
            if (!TryClaim(epoch, preferred, out int index)) return;
            DrainClaimed(scheduler, epoch, index);
        }

        private void DrainClaimed(Scheduler scheduler, uint epoch, int index)
        {
            Interlocked.Increment(ref Participants);
            TWork template = _work;
            TItem[] items = _items!;
            Scheduler? previousScheduler = s_executing;
            JobHandle access = _access;
            bool changeScope = access.Index != 0;
            ScopeToken previousScope = default;
            if (changeScope)
            {
                previousScope = s_currentScope;
                s_currentScope = ScopeToken.FromHandle(access);
            }
            s_executing = scheduler;
            int previousDepth = s_synchronousParallelDepth;
            s_synchronousParallelDepth++;
            JobThreads.Enter();
            try
            {
                do
                {
                    try
                    {
                        TWork work = template;
                        work.Execute(in items[index]);
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref Faults);
                        Interlocked.CompareExchange(ref Error, ExceptionDispatchInfo.Capture(exception), null);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref Remaining);
                    }
                } while (TryClaim(epoch, -1, out index));
            }
            finally
            {
                if (changeScope) s_currentScope = previousScope;
                s_executing = previousScheduler;
                JobThreads.Exit();
                s_synchronousParallelDepth = previousDepth;
                Interlocked.Decrement(ref Participants);
                scheduler._workQueue.Pulse();
            }
        }
    }

}
