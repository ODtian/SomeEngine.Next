using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    /// <summary>Consumes a ValueTask once and connects its terminal result to Job dependencies.</summary>
    public JobHandle CreateExternalFenceHandle(ValueTask task, ReadOnlySpan<JobResourceAccess> accesses = default) =>
        CreateExternalFenceHandle(task.AsTask(), accesses);

    /// <summary>Connects a Task's terminal result to Job dependencies and resource use.</summary>
    public JobHandle CreateExternalFenceHandle(Task task, ReadOnlySpan<JobResourceAccess> accesses = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return CreateExternalFenceHandle(new TaskFence(task), accesses);
    }

    public JobHandle CreateExternalFenceHandle(IJobExternalFence fence) =>
        CreateExternalFenceHandle(fence, ReadOnlySpan<JobResourceAccess>.Empty);

    private const string MissingExternalFenceSignalStateMessage = "External fence signal state is missing.";
    public JobHandle CreateExternalFenceHandle(
        IJobExternalFence fence,
        ReadOnlySpan<JobResourceAccess> accesses)
    {
        ArgumentNullException.ThrowIfNull(fence);
        using Submission submissionScope = EnterSubmission();
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

            fence.OnSignaled(ExternalFenceSignal, new ExternalFenceSignalState(this, state, fence));

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
        finally { submission.Release(); }
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

        _completion.TryBeginWait(handle, out CompletionLease observation);
        try
        {
            if (!TryAddExternalContinuation(handle, observer))
            {
                observer.InvokeAndSuppressObserverExceptions();
            }
        }
        finally
        {
            _completion.EndWait(observation);
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
        try
        {
            signal.Fence.GetResult();
        }
        catch (Exception error)
        {
            signal.Scheduler._completion.RecordFault(signal.Handle, ExceptionDispatchInfo.Capture(error));
        }
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
        internal ExternalFenceSignalState(Scheduler scheduler, JobHandle handle, IJobExternalFence fence)
        {
            Scheduler = scheduler;
            Handle = handle;
            Fence = fence;
        }

        internal Scheduler Scheduler { get; }

        internal JobHandle Handle { get; }

        internal IJobExternalFence Fence { get; }
    }

    private sealed class TaskFence(Task task) : IJobExternalFence
    {
        public bool IsSignaled => task.IsCompleted;

        public void OnSignaled(Action<object?> continuation, object? state)
        {
            var awaiter = task.ConfigureAwait(false).GetAwaiter();
            if (awaiter.IsCompleted)
                continuation(state);
            else
                awaiter.UnsafeOnCompleted(() => continuation(state));
        }

        public void GetResult() => task.GetAwaiter().GetResult();
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



