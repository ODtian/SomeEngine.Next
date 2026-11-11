using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal interface IStagedJob : IJob
{
    static abstract int StageCount { get; }
    void Prepare(int stage, ref AccessBuilder<JobResourceAccess> accesses);
    void Release();
}

public sealed partial class Scheduler
{
    private interface ICompletionData
    {
        void Release();
    }

    internal void AppendSynchronousAccess(JobHandle handle, in AccessBuilder<JobResourceAccess> accesses)
    {
        if (accesses.Count == 0) return;
        _completion.BeginAccessStage(handle);
        try
        {
            ResourceAccessRegistration registration = _resources.RegisterAccesses(handle, accesses, typeof(SynchronousResourceAccess));
            SetResourceAccesses(handle, registration);
            RegisterExternalDependencies(handle, registration);
        }
        catch (Exception exception)
        {
            RecordFaultAndCancelPendingWork(handle, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            SealScheduleDependencies(handle);
            ReleaseExternalDependency(handle, null);
        }
        WaitForSynchronousAdmission(handle);
    }

    internal void AppendSynchronousAccess(JobHandle handle, ReadOnlySpan<JobResourceAccess> accesses)
    {
        if (accesses.IsEmpty) return;
        _completion.BeginAccessStage(handle);
        try
        {
            ResourceAccessRegistration registration = _resources.RegisterAccesses(handle, accesses, typeof(SynchronousResourceAccess));
            SetResourceAccesses(handle, registration);
            RegisterExternalDependencies(handle, registration);
        }
        catch (Exception exception)
        {
            RecordFaultAndCancelPendingWork(handle, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            SealScheduleDependencies(handle);
            ReleaseExternalDependency(handle, null);
        }
        WaitForSynchronousAdmission(handle);
    }

    internal JobHandle ScheduleStages<T>(in T job, JobHandle dependency = default)
        where T : struct, IStagedJob
    {
        bool retained = false;
        try { return ScheduleStagesCore(job, dependency, ref retained); }
        catch
        {
            if (!retained) { T unstarted = job; unstarted.Release(); }
            throw;
        }
    }

    private JobHandle ScheduleStagesCore<T>(in T job, JobHandle dependency, ref bool retained)
        where T : struct, IStagedJob
    {
        using Submission submission = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        ValidateDependency(dependency);
        JobHandle handle = CreateState(pendingWork: 1, pendingDependencies: 1, scheduleDependenciesSealed: false);
        StagedJobData<T> data = StagedJobData<T>.Rent(this, handle, job);
        _completion.SetJobData(handle, data);
        retained = true;
        WorkBatch work = default;
        try
        {
            AttachToCurrentScope(handle);
            work = data.PrepareTicket();
            RegisterScheduleDependency(handle, dependency, work, waitForWorkOnly: false, ignoreDependencyFault: false);
            SealScheduleDependencies(handle);
            ReleaseScheduleDependency(handle, work, null);
            return handle;
        }
        catch
        {
            work.ReleaseJobs();
            CancelUnscheduledState(handle);
            throw;
        }
        finally { data.ReleaseReference(); }
    }

    private sealed partial class CompletionStore
    {
        internal void SetJobData(JobHandle handle, ICompletionData data)
        {
            CompletionState state = GetState(handle)!;
            lock (state.Sync) state.JobData = data;
        }

        internal ICompletionData? TakeJobData(JobHandle handle)
        {
            CompletionState state = GetState(handle)!;
            lock (state.Sync)
            {
                ICompletionData? data = state.JobData;
                state.JobData = null;
                return data;
            }
        }

        internal void BeginAccessStage(JobHandle handle)
        {
            CompletionState state = GetState(handle)!;
            lock (state.Sync)
            {
                state.PendingDependencies = 1;
                state.ScheduleDependenciesSealed = false;
            }
        }

        internal bool SetConcurrentExecution(JobHandle handle, bool concurrent)
        {
            CompletionState state = GetState(handle)!;
            lock (state.Sync)
            {
                bool previous = state.MayExecuteConcurrently;
                state.MayExecuteConcurrently = concurrent;
                return previous;
            }
        }
    }

    private sealed class StagedJobData<T> : ICompletionData where T : struct, IStagedJob
    {
        private static readonly object FreeLock = new();
        private static StagedJobData<T>[]? s_free;
        private static int s_freeCount;

        private Scheduler _scheduler = null!;
        internal JobHandle Handle;
        private T _job;
        private int _stage;
        private int _references;
        private int _released;

        internal static StagedJobData<T> Rent(Scheduler scheduler, JobHandle handle, in T job)
        {
            StagedJobData<T>? data = null;
            lock (FreeLock)
            {
                if (s_freeCount != 0)
                {
                    int index = --s_freeCount;
                    data = s_free![index];
                    s_free[index] = null!;
                }
            }
            data ??= new();
            data._scheduler = scheduler;
            data.Handle = handle;
            data._job = job;
            data._stage = 0;
            data._references = 2;
            data._released = 0;
            return data;
        }

        internal WorkBatch PrepareTicket()
        {
            Interlocked.Increment(ref _references);
            WorkStream<StagedJobTicket<T>> stream = _scheduler.GetWorkStream<StagedJobTicket<T>>();
            int index = stream.Prepare(new StagedJobTicket<T>(this));
            return WorkBatch.CreateSingle(stream, index, JobPriority.Normal);
        }

        private void ReleaseJob()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _job.Release();
        }

        void ICompletionData.Release()
        {
            try { ReleaseJob(); }
            finally { ReleaseReference(); }
        }

        public void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            ReleaseJob();
            _job = default;
            _scheduler = null!;
            Handle = default;
            lock (FreeLock)
            {
                StagedJobData<T>[] free = s_free ??= new StagedJobData<T>[4];
                if (s_freeCount == free.Length)
                {
                    Array.Resize(ref free, free.Length * 2);
                    s_free = free;
                }
                free[s_freeCount++] = this;
            }
        }

        internal void Execute()
        {
            Scheduler scheduler = _scheduler;
            JobHandle handle = Handle;
            if (_stage == T.StageCount)
            {
                ExceptionDispatchInfo? error = null;
                try { _job.Execute(); }
                catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
                scheduler.CompleteStreamItems(handle, 1, error);
                return;
            }

            AccessBuilder<JobResourceAccess> accesses = new();
            WorkBatch next = default;
            bool barrier = false;
            try
            {
                _job.Prepare(_stage++, ref accesses);
                scheduler._completion.BeginAccessStage(handle);
                barrier = true;
                ResourceAccessRegistration registration = scheduler._resources.RegisterAccesses(handle, accesses, typeof(T));
                scheduler.SetResourceAccesses(handle, registration);
                next = PrepareTicket();
                for (int i = 0; i < registration.DependencyCount; i++)
                {
                    ResourceDependency dependency = registration.GetDependency(i);
                    scheduler.RegisterScheduleDependency(handle, dependency.Handle, next,
                        dependency.WaitForWorkOnly, ignoreDependencyFault: dependency.WaitForWorkOnly);
                }
            }
            catch (Exception exception)
            {
                next.ReleaseJobs();
                scheduler.RecordFaultAndCancelPendingWork(handle, ExceptionDispatchInfo.Capture(exception));
                if (!barrier)
                {
                    scheduler.TryReleaseWorkDependencies(handle);
                    scheduler.TryCompleteState(handle);
                }
            }
            finally
            {
                accesses.Clear();
                if (barrier)
                {
                    scheduler.SealScheduleDependencies(handle);
                    scheduler.ReleaseScheduleDependency(handle, next, null);
                }
            }
        }
    }

    private struct StagedJobTicket<T>(StagedJobData<T> data) : IWorkStreamItem<StagedJobTicket<T>>
        where T : struct, IStagedJob
    {
        private StagedJobData<T> _data = data;
        public static bool AllowBatchDrain => false;
        public static int CompletionCountOnRetire => 0;
        public static bool ManagesCompletion => true;
        public static JobHandle GetState(in StagedJobTicket<T> item) => item._data.Handle;
        public static int Abandon(ref StagedJobTicket<T> item) => 1;
        public static WorkItemExecutionResult Execute(ref StagedJobTicket<T> item, Scheduler scheduler)
        {
            item._data.Execute();
            return new WorkItemExecutionResult(retire: true, completedWorkItems: 1);
        }
        public static void Release(ref StagedJobTicket<T> item)
        {
            StagedJobData<T> data = item._data;
            item = default;
            data.ReleaseReference();
        }
    }
}
