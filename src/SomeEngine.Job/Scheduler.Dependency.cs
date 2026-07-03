using System.Buffers;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const int InlineDependencyCapacity = 16;
    private const string UninitializedContinuationMessage = "Continuation is not initialized.";
    internal JobHandle CombineDependencies(ReadOnlySpan<JobHandle> handles)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        JobHandle[]? rentedHandles = null;
        Span<JobHandle> liveDependencies = handles.Length <= InlineDependencyCapacity
            ? stackalloc JobHandle[handles.Length]
            : (rentedHandles = ArrayPool<JobHandle>.Shared.Rent(handles.Length)).AsSpan(0, handles.Length);
        int liveDependencyCount = 0;

        try
        {
            var completedFault = CollectLiveDependencies(handles, liveDependencies, out liveDependencyCount);

            if (liveDependencyCount == 0)
                return CreateResolvedDependency(completedFault);

            JobHandle combined = CreateState(pendingWork: 0, liveDependencyCount);
            if (completedFault is not null)
                RecordFault(combined, completedFault);

            RegisterCombinedDependencies(liveDependencies[..liveDependencyCount], combined);
            return combined;
        }
        finally
        {
            if (rentedHandles is not null)
            {
                Array.Clear(rentedHandles, 0, liveDependencyCount);
                ArrayPool<JobHandle>.Shared.Return(rentedHandles);
            }
        }
    }

    private ExceptionDispatchInfo? CollectLiveDependencies(
        ReadOnlySpan<JobHandle> handles,
        Span<JobHandle> liveDependencies,
        out int liveDependencyCount)
    {
        liveDependencyCount = 0;
        ExceptionDispatchInfo? completedFault = null;
        foreach (JobHandle handle in handles)
        {
            if (!TryAddLiveDependency(handle, liveDependencies[..liveDependencyCount], ref completedFault))
                continue;

            liveDependencies[liveDependencyCount++] = handle;
        }

        return completedFault;
    }

    private bool TryAddLiveDependency(
        JobHandle handle,
        ReadOnlySpan<JobHandle> existing,
        ref ExceptionDispatchInfo? completedFault)
    {
        if (handle.Index == 0 || ContainsHandle(existing, handle))
            return false;

        if (_completion.TryGetCompletedFault(handle, markObserved: false, out var fault))
        {
            completedFault ??= fault;
            return false;
        }

        return !IsCompleted(handle);
    }

    private JobHandle CreateResolvedDependency(ExceptionDispatchInfo? completedFault)
    {
        if (completedFault is null)
            return default;

        JobHandle faulted = CreateState(pendingWork: 0, pendingDependencies: 0);
        FaultAndComplete(faulted, completedFault);
        return faulted;
    }

    private void RegisterCombinedDependencies(
        ReadOnlySpan<JobHandle> liveDependencies,
        JobHandle combined)
    {
        for (int i = 0; i < liveDependencies.Length; i++)
        {
            if (!_dependencies.AddContinuation(
                    liveDependencies[i],
                    waitForWorkOnly: false,
                    DependencyContinuation.CreateCombined(this, combined),
                    out var immediateFault))
            {
                CompleteCombinedDependency(combined, immediateFault);
            }
        }
    }

    private static bool ContainsHandle(ReadOnlySpan<JobHandle> handles, JobHandle candidate)
    {
        for (int i = 0; i < handles.Length; i++)
        {
            JobHandle handle = handles[i];
            if (handle.Index == candidate.Index
                && handle.Version == candidate.Version
                && handle.Generation == candidate.Generation)
            {
                return true;
            }
        }

        return false;
    }

    private bool RegisterScheduleDependencies(
        JobHandle state,
        JobHandle explicitDependency,
        ResourceAccessRegistration registration,
        WorkBatch work)
    {
        RegisterScheduleDependency(state, explicitDependency, work, waitForWorkOnly: false);
        for (int i = 0; i < registration.DependencyCount; i++)
        {
            ResourceDependency dependency = registration.GetDependency(i);
            RegisterScheduleDependency(state, dependency.Handle, work, dependency.WaitForWorkOnly);
        }

        return SealScheduleDependencies(state);
    }

    private bool RegisterScheduleDependency(
        JobHandle state,
        JobHandle dependency,
        WorkBatch work,
        bool waitForWorkOnly)
    {
        bool satisfied = RegisterDependencyCore(
            state,
            dependency,
            waitForWorkOnly,
            DependencyContinuation.CreateSchedule(this, state, work),
            out var shouldRelease,
            out ExceptionDispatchInfo? immediateFault);

        if (shouldRelease)
        {
            ReleaseScheduleDependency(state, work, immediateFault);
        }

        return satisfied;
    }

    private bool RegisterDependencyCore(
        JobHandle state,
        JobHandle dependency,
        bool waitForWorkOnly,
        DependencyContinuation continuation,
        out bool shouldRelease,
        out ExceptionDispatchInfo? immediateFault)
    {
        shouldRelease = false;
        immediateFault = null;

        if (dependency.Index == 0 || _dependencies.IsSatisfied(dependency, waitForWorkOnly))
        {
            return true;
        }

        if (!_dependencies.AddPending(state))
        {
            return false;
        }

        if (!_dependencies.AddContinuation(
                dependency,
                waitForWorkOnly,
                continuation,
                out immediateFault))
        {
            shouldRelease = true;
        }

        return false;
    }

    private void RegisterExternalDependencies(
        JobHandle state,
        ResourceAccessRegistration registration)
    {
        for (int i = 0; i < registration.DependencyCount; i++)
        {
            ResourceDependency dependency = registration.GetDependency(i);
            RegisterExternalDependency(state, dependency.Handle, dependency.WaitForWorkOnly);
        }
    }

    private void RegisterExternalDependency(JobHandle state, JobHandle dependency, bool waitForWorkOnly)
    {
        RegisterDependencyCore(
            state,
            dependency,
            waitForWorkOnly,
            DependencyContinuation.CreateExternal(this, state),
            out var shouldRelease,
            out ExceptionDispatchInfo? immediateFault);

        if (shouldRelease)
        {
            ReleaseExternalDependency(state, immediateFault);
        }
    }

    private bool SealScheduleDependencies(JobHandle handle)
    {
        DependencyReleaseAction action = _dependencies.SealSchedule(handle);
        if (action == DependencyReleaseAction.Complete)
        {
            TryReleaseWorkDependencies(handle);
            TryCompleteState(handle);
        }

        return action == DependencyReleaseAction.Enqueue;
    }

    private void ReleaseScheduleDependency(
        JobHandle stateHandle,
        WorkBatch work,
        ExceptionDispatchInfo? dependencyFault)
    {
        DependencyReleaseAction action = ReleaseDependencyAndGetAction(
            stateHandle,
            dependencyFault,
            cancelPendingWorkOnFault: true,
            enqueueWhenReady: true);

        if (action == DependencyReleaseAction.Enqueue)
        {
            try
            {
                _workQueue.Enqueue(work);
            }
            catch (Exception ex)
            {
                work.ReleaseJobs();
                RecordFaultAndCancelPendingWork(stateHandle, ExceptionDispatchInfo.Capture(ex));
                TryReleaseWorkDependencies(stateHandle);
                TryCompleteState(stateHandle);
            }
        }
        else if (action == DependencyReleaseAction.Complete)
        {
            work.ReleaseJobs();
            TryReleaseWorkDependencies(stateHandle);
            TryCompleteState(stateHandle);
        }
    }

    private void CompleteCombinedDependency(JobHandle combinedHandle, ExceptionDispatchInfo? dependencyFault)
    {
        ReleaseDependencyAndGetAction(
            combinedHandle,
            dependencyFault,
            cancelPendingWorkOnFault: false,
            enqueueWhenReady: false);
        TryCompleteState(combinedHandle);
    }

    private void ReleaseExternalDependency(JobHandle externalHandle, ExceptionDispatchInfo? dependencyFault)
    {
        ReleaseDependencyAndGetAction(
            externalHandle,
            dependencyFault,
            cancelPendingWorkOnFault: true,
            enqueueWhenReady: false);
        TryReleaseWorkDependencies(externalHandle);
        TryCompleteState(externalHandle);
    }

    private DependencyReleaseAction ReleaseDependencyAndGetAction(
        JobHandle stateHandle,
        ExceptionDispatchInfo? dependencyFault,
        bool cancelPendingWorkOnFault,
        bool enqueueWhenReady)
    {
        return _dependencies.ReleaseDependency(
            stateHandle,
            dependencyFault,
            cancelPendingWorkOnFault,
            enqueueWhenReady);
    }

    private readonly struct DependencyContinuation
    {
        private readonly Scheduler? _scheduler;
        private readonly JobHandle _target;
        private readonly WorkBatch _work;
        private readonly ContinuationKind _kind;

        private DependencyContinuation(
            Scheduler scheduler,
            ContinuationKind kind,
            JobHandle target,
            WorkBatch work)
        {
            _scheduler = scheduler;
            _kind = kind;
            _target = target;
            _work = work;
        }

        internal static DependencyContinuation CreateSchedule(
            Scheduler scheduler,
            JobHandle target,
            WorkBatch work)
        {
            return new DependencyContinuation(scheduler, ContinuationKind.Schedule, target, work);
        }

        internal static DependencyContinuation CreateCombined(Scheduler scheduler, JobHandle target)
        {
            return new DependencyContinuation(scheduler, ContinuationKind.Combine, target, default);
        }

        internal static DependencyContinuation CreateExternal(Scheduler scheduler, JobHandle target)
        {
            return new DependencyContinuation(scheduler, ContinuationKind.External, target, default);
        }

        internal void Invoke(ExceptionDispatchInfo? fault)
        {
            Scheduler scheduler = _scheduler ?? throw new InvalidOperationException(UninitializedContinuationMessage);
            if (_kind == ContinuationKind.Combine)
            {
                scheduler.CompleteCombinedDependency(_target, fault);
            }
            else if (_kind == ContinuationKind.External)
            {
                scheduler.ReleaseExternalDependency(_target, fault);
            }
            else
            {
                scheduler.ReleaseScheduleDependency(_target, _work, fault);
            }
        }
    }

    private enum ContinuationKind
    {
        Schedule,
        Combine,
        External
    }

    private enum DependencyReleaseAction
    {
        None,
        Enqueue,
        Complete
    }
}



