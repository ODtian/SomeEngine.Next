namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    private const string NestedSynchronousAccessMessage =
        "A running Job cannot acquire a nested synchronous Resource Owner. Declare the resource on the " +
        "Job and use RequireCurrentAccess from the owner-specific API instead.";

    internal SynchronousResourceAccess AcquireSynchronousAccess(
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        using Submission submissionScope = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        if (s_currentScope.Index != 0)
            throw new InvalidOperationException(NestedSynchronousAccessMessage);

        if (accesses.Length == 0)
            return default;

        JobHandle state = CreateState(pendingWork: 1, pendingDependencies: 0);
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        try
        {
            registration = _resources.RegisterAccesses(state, accesses, typeof(SynchronousResourceAccess));
            if (!SetResourceAccesses(state, registration)) registration = ResourceAccessRegistration.Empty;
            RegisterExternalDependencies(state, registration);
        }
        catch
        {
            _resources.ReleaseAccesses(registration, state);
            CancelUnscheduledState(state);
            throw;
        }

        try
        {
            WaitForSynchronousAdmission(state);
            return new SynchronousResourceAccess(this, state);
        }
        catch
        {
            ReleaseSynchronousAccess(state);
            throw;
        }
    }

    internal SynchronousResourceAccess AcquireSynchronousAccess(
        JobResourceAccess first, in AccessBuilder<JobResourceAccess> following)
    {
        ReadOnlySpan<JobResourceAccess> firstStage = stackalloc JobResourceAccess[] { first };
        if (following.Count == 0) return AcquireSynchronousAccess(firstStage);
        return AcquireSynchronousAccess(firstStage, following);
    }

    private SynchronousResourceAccess AcquireSynchronousAccess(
        ReadOnlySpan<JobResourceAccess> accesses, in AccessBuilder<JobResourceAccess> following)
    {
        using Submission submissionScope = EnterSubmission();
        EnsureCurrentScopeBelongsToThisRuntime();
        if (s_currentScope.Index != 0)
            throw new InvalidOperationException(NestedSynchronousAccessMessage);

        JobHandle state = CreateState(pendingWork: 1, pendingDependencies: 0);
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        ResourceAccessRegistration next = ResourceAccessRegistration.Empty;
        bool followingRegistered;
        try
        {
            (registration, next, followingRegistered) = _resources.RegisterAccesses(state, accesses, following, typeof(SynchronousResourceAccess));
            if (!SetResourceAccesses(state, registration)) registration = ResourceAccessRegistration.Empty;
            if (!SetResourceAccesses(state, next)) next = ResourceAccessRegistration.Empty;
            RegisterExternalDependencies(state, registration);
            if (next.Data is not null) RegisterExternalDependencies(state, next);
        }
        catch
        {
            _resources.ReleaseAccesses(next, state);
            _resources.ReleaseAccesses(registration, state);
            CancelUnscheduledState(state);
            throw;
        }

        try
        {
            WaitForSynchronousAdmission(state);
            if (!followingRegistered) AppendSynchronousAccess(state, following);
            return new SynchronousResourceAccess(this, state);
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
        while (true)
        {
            long wakeVersion = _workQueue.WakeVersion;
            if (_execution.CanExecute(handle)) break;
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

            _workQueue.WaitAndExecuteOne(wakeVersion);

            spinWait = new SpinWait();
        }
    }
}
