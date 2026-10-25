using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    [ThreadStatic]
    private static int s_synchronousParallelDepth;

    /// <summary>Executes the ranges on this scheduler and the calling thread, returning after every participant exits.</summary>
    public void ExecuteParallel<T>(in T job, int length, int batchSize = 1,
        JobScheduleOptions options = default) where T : struct, IJobParallelFor =>
        ExecuteParallel(job, length, batchSize, ReadOnlySpan<JobResourceAccess>.Empty, options);

    /// <summary>Holds the declared accesses through synchronous range execution. Nested calls use the current scope's covering accesses.</summary>
    public void ExecuteParallel<T>(in T job, int length, int batchSize,
        ReadOnlySpan<JobResourceAccess> accesses, JobScheduleOptions options = default)
        where T : struct, IJobParallelFor
    {
        using Submission submission = EnterSubmission();
        BeginParallelSchedule<T>(options, batchSize);
        if (length <= 0)
            return;
        int resolvedSize = batchSize == JobScheduleOptions.AutomaticBatchSize
            ? ParallelBatchSizer<T>.Resolve(length, _config.WorkerCount,
                _config.AutoBatchTargetMicroseconds, _config.AutoBatchMaxTilesPerWorker)
            : batchSize;
        int batchCount = CalculateParallelBatchCount(length, resolvedSize);
        int helpers = Math.Min(_config.WorkerCount, batchCount - 1);
        SynchronousResourceOwner resources = default;
        ScopeToken scope = s_currentScope;
        if (JobExecutionContext.IsActive)
        {
            foreach (JobResourceAccess access in accesses)
                RequireCurrentAccess(access, requireSingleWorkItem: false);
        }
        else
        {
            resources = accesses.IsEmpty ? default : AcquireSynchronousAccess(accesses);
            scope = ScopeToken.FromHandle(resources.Handle);
        }
        SynchronousBatch<T> batch = SynchronousBatch<T>.Rent(this, job, length, resolvedSize, helpers, scope);
        ExceptionDispatchInfo? failure = null;
        try
        {
            if (helpers != 0)
                _workQueue.EnqueueSynchronous(batch, helpers, options);
            batch.RunCaller();
            SpinWait spin = new();
            while (!batch.IsFinished)
            {
                long wake = _workQueue.WakeVersion;
                if (batch.IsFinished)
                    break;
                if (_workQueue.TryExecuteOne(wait: false))
                    spin.Reset();
                else if (!spin.NextSpinWillYield)
                    spin.SpinOnce();
                else
                    _workQueue.WaitAndExecuteOne(wake);
            }
            failure = batch.Failure;
            if (failure is not null && resources.Handle.Index != 0)
                RecordFault(resources.Handle, failure);
        }
        finally
        {
            batch.Return();
            resources.Dispose();
        }
        if (resources.Handle.Index != 0)
        {
            try { Complete(resources.Handle); }
            catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        }
        failure?.Throw();
    }

    private sealed class SynchronousBatch<T> : QueuedWork where T : struct, IJobParallelFor
    {
        [ThreadStatic] private static SynchronousBatch<T>? s_available;
        private SynchronousBatch<T>? _next;
        private Scheduler _scheduler = null!;
        private ScopeToken _scope;
        private T _job;
        private int _length;
        private int _batchSize;
        private int _nextBatch;
        private int _remaining;
        private ExceptionDispatchInfo? _failure;

        internal static SynchronousBatch<T> Rent(Scheduler scheduler, in T job, int length, int batchSize, int helpers,
            ScopeToken scope)
        {
            SynchronousBatch<T> batch = s_available ?? new SynchronousBatch<T>();
            s_available = batch._next;
            batch._next = null;
            batch._scheduler = scheduler;
            batch._scope = scope;
            batch._job = job;
            batch._length = length;
            batch._batchSize = batchSize;
            batch._nextBatch = 1;
            batch._remaining = helpers + 1;
            return batch;
        }

        internal bool IsFinished => Volatile.Read(ref _remaining) == 0;
        internal ExceptionDispatchInfo? Failure => _failure;

        internal void Return()
        {
            _job = default;
            _scheduler = null!;
            _scope = default;
            _failure = null;
            _next = s_available;
            s_available = this;
        }

        internal void RunCaller()
        {
            RunParticipant(callingThread: true);
            DrainExited();
        }

        private void RunParticipant(bool callingThread)
        {
            Scheduler? previousScheduler = s_executing;
            ScopeToken previousScope = s_currentScope;
            int previousParallelDepth = s_synchronousParallelDepth;
            s_executing = _scheduler;
            s_currentScope = _scope;
            s_synchronousParallelDepth++;
            JobExecutionContext.Enter();
            try
            {
                if (callingThread)
                    RunBatch(0);
                Drain();
            }
            finally
            {
                JobExecutionContext.Exit();
                s_currentScope = previousScope;
                s_synchronousParallelDepth = previousParallelDepth;
                s_executing = previousScheduler;
            }
        }

        private void Drain()
        {
            while (true)
            {
                int index = Interlocked.Increment(ref _nextBatch) - 1;
                if ((long)index * _batchSize >= _length)
                    return;
                RunBatch(index);
            }
        }

        private void RunBatch(int batchIndex)
        {
            T job = _job;
            int start = batchIndex * _batchSize;
            int end = Math.Min(start + _batchSize, _length);
            try
            {
                for (int index = start; index < end; index++)
                    job.Execute(index);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(error), null);
            }
        }

        internal override int DrainAndFinish(Scheduler scheduler, int maxItems, int priorityIndex,
            int maxDrainers, out int retiredItems, out bool hasMoreWork)
        {
            RunParticipant(callingThread: false);
            retiredItems = 1;
            hasMoreWork = false;
            return 1;
        }

        internal override void DrainExited()
        {
            Scheduler scheduler = _scheduler;
            if (Interlocked.Decrement(ref _remaining) == 0)
                scheduler._workQueue.Pulse();
        }

    }
}
