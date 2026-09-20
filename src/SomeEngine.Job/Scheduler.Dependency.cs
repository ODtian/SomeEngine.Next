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
        WorkBatch work,
        bool ignoreExplicitDependencyFault = false)
    {
        RegisterScheduleDependency(
            state,
            explicitDependency,
            work,
            waitForWorkOnly: false,
            ignoreDependencyFault: ignoreExplicitDependencyFault);
        for (int i = 0; i < registration.DependencyCount; i++)
        {
            ResourceDependency dependency = registration.GetDependency(i);
            RegisterScheduleDependency(
                state,
                dependency.Handle,
                work,
                dependency.WaitForWorkOnly,
                ignoreDependencyFault: dependency.WaitForWorkOnly);
        }

        return SealScheduleDependencies(state);
    }

    private void RegisterDeferredResourceScheduleDependency(
        JobHandle state,
        JobHandle explicitDependency,
        ResourceAccessReservation reservation,
        WorkBatch work)
    {
        if (!_dependencies.AddPending(state))
        {
            throw new InvalidOperationException(
                "Deferred resource admission could not retain its scheduled state.");
        }

        if (!_dependencies.AddContinuation(
                explicitDependency,
                waitForWorkOnly: false,
                DependencyContinuation.CreateResourceAdmission(
                    this,
                    state,
                    work,
                    reservation),
                out ExceptionDispatchInfo? immediateFault))
        {
            CompleteDeferredResourceAdmission(
                state,
                work,
                reservation,
                immediateFault);
        }
    }

    private void CompleteDeferredResourceAdmission(
        JobHandle state,
        WorkBatch work,
        ResourceAccessReservation reservation,
        ExceptionDispatchInfo? explicitDependencyFault)
    {
        ExceptionDispatchInfo? admissionFault = explicitDependencyFault;
        if (admissionFault is null)
        {
            try
            {
                ResourceAccessRegistration registration =
                    _resources.ActivateReservation(state, reservation);
                if (!SetResourceAccesses(state, registration))
                {
                    work.ReleaseJobs();
                    return;
                }

                for (int i = 0; i < registration.DependencyCount; i++)
                {
                    ResourceDependency dependency = registration.GetDependency(i);
                    RegisterScheduleDependency(
                        state,
                        dependency.Handle,
                        work,
                        dependency.WaitForWorkOnly,
                        ignoreDependencyFault: dependency.WaitForWorkOnly);
                }
            }
            catch (Exception exception)
            {
                admissionFault = ExceptionDispatchInfo.Capture(exception);
            }
        }

        CancelReservation(reservation);

        // The explicit full dependency remains counted until resource activation has published
        // every work-only hazard. Sealing before releasing that final count prevents a transient
        // enqueue between the two dependency classes.
        SealScheduleDependencies(state);
        ReleaseScheduleDependency(state, work, admissionFault);
    }

    private bool RegisterScheduleDependency(
        JobHandle state,
        JobHandle dependency,
        WorkBatch work,
        bool waitForWorkOnly,
        bool ignoreDependencyFault)
    {
        bool satisfied = RegisterDependencyCore(
            state,
            dependency,
            waitForWorkOnly,
            DependencyContinuation.CreateSchedule(
                this,
                state,
                work,
                ignoreDependencyFault),
            out var shouldRelease,
            out ExceptionDispatchInfo? immediateFault);

        if (shouldRelease)
        {
            ReleaseScheduleDependency(
                state,
                work,
                ignoreDependencyFault ? null : immediateFault);
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
            RegisterExternalDependency(
                state,
                dependency.Handle,
                dependency.WaitForWorkOnly,
                ignoreDependencyFault: dependency.WaitForWorkOnly);
        }
    }

    private void RegisterExternalDependency(
        JobHandle state,
        JobHandle dependency,
        bool waitForWorkOnly,
        bool ignoreDependencyFault)
    {
        RegisterDependencyCore(
            state,
            dependency,
            waitForWorkOnly,
            DependencyContinuation.CreateExternal(this, state, ignoreDependencyFault),
            out var shouldRelease,
            out ExceptionDispatchInfo? immediateFault);

        if (shouldRelease)
        {
            ReleaseExternalDependency(state, ignoreDependencyFault ? null : immediateFault);
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
        private readonly ResourceAccessReservation _reservation;
        private readonly ContinuationKind _kind;
        private readonly bool _ignoreDependencyFault;

        private DependencyContinuation(
            Scheduler scheduler,
            ContinuationKind kind,
            JobHandle target,
            WorkBatch work,
            ResourceAccessReservation reservation = default,
            bool ignoreDependencyFault = false)
        {
            _scheduler = scheduler;
            _kind = kind;
            _target = target;
            _work = work;
            _reservation = reservation;
            _ignoreDependencyFault = ignoreDependencyFault;
        }

        internal static DependencyContinuation CreateSchedule(
            Scheduler scheduler,
            JobHandle target,
            WorkBatch work,
            bool ignoreDependencyFault)
        {
            return new DependencyContinuation(
                scheduler,
                ContinuationKind.Schedule,
                target,
                work,
                ignoreDependencyFault: ignoreDependencyFault);
        }

        internal static DependencyContinuation CreateCombined(Scheduler scheduler, JobHandle target)
        {
            return new DependencyContinuation(scheduler, ContinuationKind.Combine, target, default);
        }

        internal static DependencyContinuation CreateResourceAdmission(
            Scheduler scheduler,
            JobHandle target,
            WorkBatch work,
            ResourceAccessReservation reservation)
        {
            return new DependencyContinuation(
                scheduler,
                ContinuationKind.ResourceAdmission,
                target,
                work,
                reservation);
        }

        internal static DependencyContinuation CreateExternal(
            Scheduler scheduler,
            JobHandle target,
            bool ignoreDependencyFault)
        {
            return new DependencyContinuation(
                scheduler,
                ContinuationKind.External,
                target,
                default,
                ignoreDependencyFault: ignoreDependencyFault);
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
                scheduler.ReleaseExternalDependency(
                    _target,
                    _ignoreDependencyFault ? null : fault);
            }
            else if (_kind == ContinuationKind.ResourceAdmission)
            {
                scheduler.CompleteDeferredResourceAdmission(
                    _target,
                    _work,
                    _reservation,
                    fault);
            }
            else
            {
                scheduler.ReleaseScheduleDependency(
                    _target,
                    _work,
                    _ignoreDependencyFault ? null : fault);
            }
        }
    }

    private enum ContinuationKind
    {
        Schedule,
        Combine,
        External,
        ResourceAdmission
    }

    private enum DependencyReleaseAction
    {
        None,
        Enqueue,
        Complete
    }
}



