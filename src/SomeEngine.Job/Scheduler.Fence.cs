namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const string MissingExternalFenceSignalStateMessage = "External fence signal state is missing.";
    internal JobHandle CreateExternalFenceHandle(
        IJobExternalFence fence,
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EnsureCurrentScopeBelongsToThisRuntime();

        JobSubmissionReservation submission = JobSubmissionTracker.Begin(this, accesses);
        JobHandle state = default;
        ResourceAccessRegistration registration = ResourceAccessRegistration.Empty;
        try
        {
            state = CreateState(pendingWork: 1, pendingDependencies: 0);
            registration = _resources.RegisterAccesses(state, accesses, fence.GetType());
            SetResourceAccesses(state, registration);
            AttachToCurrentScope(state);
            submission.Bind(state);
            RegisterExternalDependencies(state, registration);

            fence.OnSignaled(ExternalFenceSignal, new ExternalFenceSignalState(this, state));

            return state;
        }
        catch
        {
            try
            {
                ReleaseAccessesIfRegistered(registration);
                if (state.Index != 0)
                    CancelUnscheduledState(state);
            }
            finally
            {
                submission.Rollback();
            }
            throw;
        }
    }

    internal void OnCompleted(
        JobHandle handle,
        Action<JobHandle, object?> continuation,
        object? callbackState)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        ExternalCompletionContinuation observer = new(handle, continuation, callbackState);

        if (handle.Index == 0)
        {
            observer.InvokeAndSuppressObserverExceptions();
            return;
        }

        if (!TryAddExternalContinuation(handle, observer))
        {
            observer.InvokeAndSuppressObserverExceptions();
        }
    }

    private void SignalExternalFence(JobHandle handle)
    {
        if (_fences.SignalFence(handle))
        {
            TryReleaseWorkDependencies(handle);
            TryCompleteState(handle);
        }
    }

    private static void ExternalFenceSignal(object? state)
    {
        ExternalFenceSignalState signal = (ExternalFenceSignalState?)state
            ?? throw new InvalidOperationException(MissingExternalFenceSignalStateMessage);
        signal.Scheduler.SignalExternalFence(signal.Handle);
    }

    private bool TryAddExternalContinuation(
        JobHandle handle,
        ExternalCompletionContinuation continuation)
    {
        return _fences.AddExternal(handle, continuation);
    }

    private sealed class ExternalFenceSignalState
    {
        internal ExternalFenceSignalState(Scheduler scheduler, JobHandle handle)
        {
            Scheduler = scheduler;
            Handle = handle;
        }

        internal Scheduler Scheduler { get; }

        internal JobHandle Handle { get; }
    }

    private readonly struct ExternalCompletionContinuation
    {
        private readonly JobHandle _handle;
        private readonly Action<JobHandle, object?> _continuation;
        private readonly object? _state;

        internal ExternalCompletionContinuation(
            JobHandle handle,
            Action<JobHandle, object?> continuation,
            object? state)
        {
            _handle = handle;
            _continuation = continuation;
            _state = state;
        }

        internal void InvokeAndSuppressObserverExceptions()
        {
            try
            {
                _continuation(_handle, _state);
            }
            catch
            {
                // Completion observers are best-effort notifications; callback faults must not corrupt cleanup.
            }
        }
    }
}



