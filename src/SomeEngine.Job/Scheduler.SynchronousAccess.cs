namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const string NestedSynchronousAccessMessage =
        "A running Job cannot acquire a nested synchronous Resource Owner. Declare the resource on the " +
        "Job and use RequireCurrentAccess from the owner-specific API instead.";

    internal SynchronousResourceOwner AcquireSynchronousAccess(
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        if (s_currentScope.Index != 0)
            throw new InvalidOperationException(NestedSynchronousAccessMessage);

        if (accesses.Length == 0)
            return default;

        JobHandle state = CreateState(pendingWork: 1, pendingDependencies: 0);
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        try
        {
            registration = _resources.RegisterAccesses(
                state,
                accesses,
                typeof(SynchronousResourceOwner));
            SetResourceAccesses(state, registration);
            RegisterExternalDependencies(state, registration);
        }
        catch
        {
            ReleaseAccessesIfRegistered(registration);
            CancelUnscheduledState(state);
            throw;
        }

        try
        {
            WaitForSynchronousAdmission(state);
            return new SynchronousResourceOwner(this, state);
        }
        catch
        {
            // A dependency fault can already have completed and released this state. The release
            // path is idempotent for stale/completed handles, so this also covers interruption
            // between admission and returning the caller owner.
            ReleaseSynchronousAccess(state);
            throw;
        }
    }

    internal void ReleaseSynchronousAccess(JobHandle handle)
    {
        if (!_execution.CompleteItems(handle, 1, itemFault: null, out bool workFinished) || !workFinished)
            return;

        TryReleaseWorkDependencies(handle);
        TryCompleteState(handle);
    }

    private void WaitForSynchronousAdmission(JobHandle handle)
    {
        bool waited = false;
        SpinWait spinWait = new();
        while (!_execution.CanExecute(handle))
        {
            if (_completion.IsCompleted(handle))
            {
                // Complete rethrows the dependency fault and marks it observed.
                Complete(handle);
                throw new InvalidOperationException(
                    "Synchronous resource admission completed before it could be acquired.");
            }

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
                Thread.Yield();

            spinWait = new SpinWait();
        }
    }
}
