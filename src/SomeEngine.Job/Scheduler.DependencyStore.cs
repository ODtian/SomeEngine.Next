using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private sealed partial class CompletionStore
    {
        public bool IsSatisfied(JobHandle handle, bool waitForWorkOnly)
        {
            return waitForWorkOnly
                ? IsWorkCompletedSuccessfully(handle)
                : IsCompletedSuccessfully(handle);
        }

        public bool AddPending(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (state.InUse && state.Version == handle.Version && !state.Completed)
                {
                    state.PendingDependencies++;
                    return true;
                }
            }

            return false;
        }

        public bool AddContinuation(
            JobHandle dependency,
            bool waitForWorkOnly,
            DependencyContinuation continuation,
            out ExceptionDispatchInfo? immediateFault)
        {
            return waitForWorkOnly
                ? AddWorkContinuation(dependency, continuation, out immediateFault)
                : AddFullContinuation(dependency, continuation, out immediateFault);
        }

        public DependencyReleaseAction SealSchedule(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return DependencyReleaseAction.None;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || state.Completed)
                {
                    return DependencyReleaseAction.None;
                }

                state.ScheduleDependenciesSealed = true;
                if (state.PendingDependencies == 0 && state.Fault is null && state.PendingWork > 0)
                {
                    return DependencyReleaseAction.Enqueue;
                }

                return state.PendingDependencies == 0 && (state.Fault is not null || state.PendingWork == 0)
                    ? DependencyReleaseAction.Complete
                    : DependencyReleaseAction.None;
            }
        }

        public DependencyReleaseAction ReleaseDependency(
            JobHandle handle,
            ExceptionDispatchInfo? dependencyFault,
            bool cancelPendingWorkOnFault,
            bool enqueueWhenReady)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return DependencyReleaseAction.None;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || state.Completed)
                {
                    return DependencyReleaseAction.None;
                }

                state.PendingDependencies--;
                ApplyDependencyFault(state, dependencyFault, cancelPendingWorkOnFault);

                if (state.PendingDependencies != 0)
                {
                    return DependencyReleaseAction.None;
                }

                if (ShouldEnqueueAfterDependency(state, enqueueWhenReady))
                    return DependencyReleaseAction.Enqueue;

                return state.Fault is not null || state.PendingWork == 0
                    ? DependencyReleaseAction.Complete
                    : DependencyReleaseAction.None;
            }
        }

        private static void ApplyDependencyFault(
            CompletionState state,
            ExceptionDispatchInfo? dependencyFault,
            bool cancelPendingWorkOnFault)
        {
            if (dependencyFault is null)
                return;

            state.Fault ??= dependencyFault;
            if (cancelPendingWorkOnFault)
                state.PendingWork = 0;
        }

        private static bool ShouldEnqueueAfterDependency(
            CompletionState state,
            bool enqueueWhenReady)
        {
            return enqueueWhenReady &&
                   state.Fault is null &&
                   state.ScheduleDependenciesSealed;
        }

        private bool IsCompletedSuccessfully(JobHandle handle)
        {
            if (handle.Index == 0)
            {
                return true;
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return true;
            }

            if (!Volatile.Read(ref state.InUse) || Volatile.Read(ref state.Version) != handle.Version)
            {
                return true;
            }

            if (!Volatile.Read(ref state.Completed))
            {
                return false;
            }

            lock (state.Sync)
            {
                return !state.InUse || state.Version != handle.Version || (state.Completed && state.Fault is null);
            }
        }

        private bool IsWorkCompletedSuccessfully(JobHandle handle)
        {
            if (handle.Index == 0)
            {
                return true;
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return true;
            }

            if (!Volatile.Read(ref state.InUse) || Volatile.Read(ref state.Version) != handle.Version)
            {
                return true;
            }

            if (!Volatile.Read(ref state.WorkDependenciesReleased))
            {
                return false;
            }

            lock (state.Sync)
            {
                return !state.InUse
                    || state.Version != handle.Version
                    || (state.WorkDependenciesReleased && state.Fault is null);
            }
        }

        private bool AddFullContinuation(
            JobHandle dependency,
            DependencyContinuation continuation,
            out ExceptionDispatchInfo? immediateFault)
        {
            immediateFault = null;
            CompletionState? state = GetState(dependency);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != dependency.Version)
                {
                    return false;
                }

                if (state.Completed)
                {
                    immediateFault = state.Fault;
                    return false;
                }

                state.Continuations.Add(continuation);
                return true;
            }
        }

        private bool AddWorkContinuation(
            JobHandle dependency,
            DependencyContinuation continuation,
            out ExceptionDispatchInfo? immediateFault)
        {
            immediateFault = null;
            CompletionState? state = GetState(dependency);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != dependency.Version)
                {
                    return false;
                }

                if (state.WorkDependenciesReleased)
                {
                    immediateFault = state.Fault;
                    return false;
                }

                state.WorkContinuations.Add(continuation);
                return true;
            }
        }
    }
}



