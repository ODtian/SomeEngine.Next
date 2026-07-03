using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private interface ICompletionStore
    {
        bool IsCompleted(JobHandle handle);

        bool IsCompleted(CompletionLease lease);

        bool TryBeginWait(JobHandle handle, out CompletionLease lease);

        void EndWait(CompletionLease lease);

        JobHandle CreateState(int pendingWork, int pendingDependencies, bool scheduleDependenciesSealed);

        ScopeToken CancelState(JobHandle handle);

        void RecordFault(JobHandle handle, ExceptionDispatchInfo fault);

        void CancelWithFault(JobHandle handle, ExceptionDispatchInfo fault);

        bool TryGetCompletedFault(JobHandle handle, bool markObserved, out ExceptionDispatchInfo? fault);

        bool TryGetCompletedFault(CompletionLease lease, bool markObserved, out ExceptionDispatchInfo? fault);

        void ReleaseSuccess(JobHandle handle);
    }

    private interface IDependencyStore
    {
        bool IsSatisfied(JobHandle handle, bool waitForWorkOnly);

        bool AddPending(JobHandle handle);

        bool AddContinuation(
            JobHandle dependency,
            bool waitForWorkOnly,
            DependencyContinuation continuation,
            out ExceptionDispatchInfo? immediateFault);

        DependencyReleaseAction SealSchedule(JobHandle handle);

        DependencyReleaseAction ReleaseDependency(
            JobHandle handle,
            ExceptionDispatchInfo? dependencyFault,
            bool cancelPendingWorkOnFault,
            bool enqueueWhenReady);
    }

    private interface IExecutionStore
    {
        bool CanExecute(JobHandle handle);

        bool CompleteItem(JobHandle handle, ExceptionDispatchInfo? itemFault);

        WorkRelease BeginWork(JobHandle handle);

        WorkCallbacks EndWork(JobHandle handle);

        CompleteRelease BeginDispatch(JobHandle handle);

        CompleteCallbacks EndDispatch(JobHandle handle);

        bool SetResources(JobHandle handle, ResourceAccessRegistration registration);
    }

    private interface IScopeStore
    {
        bool AddResource(JobHandle owner, ScopeOwnedResource resource);

        JobHandle AttachChild(ScopeToken parent, JobHandle child);

        bool ReleaseChild(ScopeToken parent, ExceptionDispatchInfo? childFault);
    }

    private interface IFenceStore
    {
        bool SignalFence(JobHandle handle);

        bool AddExternal(JobHandle handle, ExternalCompletionContinuation continuation);
    }

    internal bool IsCompleted(JobHandle handle)
    {
        return _completion.IsCompleted(handle);
    }

    internal void Complete(JobHandle handle)
    {
        if (handle.Index == 0)
        {
            return;
        }

        if (!_completion.TryBeginWait(handle, out CompletionLease lease))
        {
            return;
        }

        try
        {
            bool waited = false;
            SpinWait spinWait = new();
            while (!_completion.IsCompleted(lease))
            {
                if (!waited)
                {
                    _counters.Waited();
                    waited = true;
                }

                if (_workQueue.TryExecuteOne(wait: false))
                {
                    spinWait = new SpinWait();
                    continue;
                }

                if (!spinWait.NextSpinWillYield)
                {
                    spinWait.SpinOnce();
                    continue;
                }

                if (!_workQueue.TryExecuteOne(wait: true))
                {
                    Thread.Yield();
                }

                spinWait = new SpinWait();
            }

            if (_completion.TryGetCompletedFault(lease, markObserved: true, out ExceptionDispatchInfo? fault)
                && fault is not null)
            {
                fault.Throw();
            }
        }
        finally
        {
            _completion.EndWait(lease);
        }
    }

    private JobHandle CreateState(
        int pendingWork,
        int pendingDependencies,
        bool scheduleDependenciesSealed = false)
    {
        return _completion.CreateState(pendingWork, pendingDependencies, scheduleDependenciesSealed);
    }

    private void CancelUnscheduledState(JobHandle handle)
    {
        ScopeToken parent = _completion.CancelState(handle);
        if (parent.Index != 0 && _scopes.ReleaseChild(parent, null))
        {
            TryCompleteState(parent.ToHandle());
        }
    }

    private void FaultAndComplete(JobHandle handle, ExceptionDispatchInfo fault)
    {
        RecordFault(handle, fault);
        TryCompleteState(handle);
    }

    private void RecordFault(JobHandle handle, ExceptionDispatchInfo fault)
    {
        _completion.RecordFault(handle, fault);
    }

    private void RecordFaultAndCancelPendingWork(JobHandle handle, ExceptionDispatchInfo fault)
    {
        _completion.CancelWithFault(handle, fault);
    }

    private void ReleaseSuccessfulState(JobHandle handle)
    {
        _completion.ReleaseSuccess(handle);
    }
}



