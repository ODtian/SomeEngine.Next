using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    internal WorkItemExecutionResult ExecuteStreamItem<TItem>(ref TItem item)
        where TItem : struct, IWorkStreamItem<TItem>
    {
        JobHandle handle = TItem.GetState(item);
        if (!_execution.CanExecute(handle))
        {
            int abandonedWorkItems = TItem.Abandon(ref item);
            TItem.Release(ref item);
            return new WorkItemExecutionResult(
                retire: true,
                completedWorkItems: abandonedWorkItems);
        }

        ScopeToken previousScope = s_currentScope;
        ScopeToken nextScope = ScopeToken.FromHandle(handle);
        JobExecutionContext.Enter();
        s_currentScope = nextScope;
        WorkItemExecutionResult result;

        try
        {
            result = TItem.Execute(ref item);
        }
        catch (Exception ex)
        {
            int canceledWorkItems = TItem.Abandon(ref item);
            result = new WorkItemExecutionResult(
                retire: true,
                completedWorkItems: canceledWorkItems,
                faultedWorkItems: 1,
                ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            try
            {
                s_currentScope = previousScope;
            }
            finally
            {
                JobExecutionContext.Exit();
            }
        }

        _counters.Executed(result.CompletedWorkItems);
        _counters.Faulted(result.FaultedWorkItems);

        if (result.Retire)
            TItem.Release(ref item);
        if (result.CompletedWorkItems > 0)
            CompleteStreamItems(handle, result.CompletedWorkItems, result.Fault);
        return result;
    }

    private void CompleteStreamItems(
        JobHandle handle,
        int completedWorkItems,
        ExceptionDispatchInfo? itemFault)
    {
        if (_execution.CompleteItems(
                handle,
                completedWorkItems,
                itemFault,
                out bool workFinished) && workFinished)
        {
            TryReleaseWorkDependencies(handle);
            TryCompleteState(handle);
        }
    }

    private void TryReleaseWorkDependencies(JobHandle handle)
    {
        WorkRelease release = _execution.BeginWork(handle);
        if (!release.Started)
        {
            return;
        }

        ReleaseAccessesIfRegistered(release.Accesses);
        WorkCallbacks callbacks = _execution.EndWork(handle);
        if (callbacks.Continuations is null)
        {
            return;
        }

        foreach (var continuation in callbacks.Continuations)
        {
            continuation.Invoke(callbacks.Fault);
        }

        callbacks.Continuations.Clear();
    }

    private void TryCompleteState(JobHandle handle)
    {
        CompleteRelease release = _execution.BeginDispatch(handle);
        if (!release.Started)
        {
            return;
        }

        if (release.Resources is not null)
        {
            _resources.ReleaseScopeOwned(release.Resources);
            release.Resources.Clear();
        }

        CompleteCallbacks callbacks = _execution.EndDispatch(handle);
        if (callbacks.Completed)
        {
            DispatchCompletedState(
                handle,
                callbacks.Continuations,
                callbacks.ExternalContinuations,
                callbacks.ExternalContinuationLease,
                callbacks.Parent,
                callbacks.Fault);
        }
    }

    private void DispatchCompletedState(
        JobHandle handle,
        List<DependencyContinuation>? continuations,
        List<ExternalCompletionContinuation>? externalContinuations,
        CompletionLease externalContinuationLease,
        ScopeToken parent,
        ExceptionDispatchInfo? fault)
    {
        if (continuations is not null)
        {
            foreach (var continuation in continuations)
            {
                continuation.Invoke(fault);
            }

            continuations.Clear();
        }

        if (parent.Index != 0)
        {
            if (_scopes.ReleaseChild(parent, fault))
            {
                TryCompleteState(parent.ToHandle());
            }
        }

        _workQueue.Pulse();

        _counters.Completed();

        if (externalContinuations is not null)
        {
            try
            {
                foreach (var continuation in externalContinuations)
                {
                    continuation.InvokeAndSuppressObserverExceptions();
                }
            }
            finally
            {
                externalContinuations.Clear();
                _completion.EndWait(externalContinuationLease);
            }
        }

        if (fault is null)
        {
            ReleaseSuccessfulState(handle);
        }
    }
}



