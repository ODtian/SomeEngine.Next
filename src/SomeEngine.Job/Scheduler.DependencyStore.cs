using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
{
    private sealed partial class CompletionStore
    {
        private readonly Lock _dependencyLock = new();
        private readonly List<JobHandle> _completionHandles = [];
        private readonly HashSet<(int Index, int Version)> _visitedCompletionKeys = [];

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

            state.RecordFirstFault(dependencyFault);
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

            lock (_dependencyLock)
            {
                RejectCompletionCycle(dependency, continuation.Target);
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
        }

        private void RejectCompletionCycle(JobHandle dependency, JobHandle target)
        {
            JobHandle parent = GetParent(target);
            if (parent.Index == 0)
                return;

            try
            {
                _completionHandles.Add(parent);
                for (int index = 0; index < _completionHandles.Count; index++)
                {
                    JobHandle current = _completionHandles[index];
                    if (!_visitedCompletionKeys.Add((current.Index, current.Version)))
                        continue;

                    if (current.Index == dependency.Index && current.Version == dependency.Version)
                        throw new InvalidOperationException(
                            "The dependency waits for the current Job or one of its ancestors.");

                    CompletionState? state = GetState(current);
                    if (state is null)
                        continue;

                    lock (state.Sync)
                    {
                        if (!state.InUse || state.Version != current.Version || state.Completed)
                            continue;

                        // Completion flows from children to their parent and from prerequisites
                        // to their continuations. Resource conflicts use the separate work list.
                        if (state.Parent.Index != 0)
                            _completionHandles.Add(state.Parent.ToHandle());
                        foreach (DependencyContinuation continuation in state.Continuations)
                            _completionHandles.Add(continuation.Target);
                    }
                }
            }
            finally
            {
                _completionHandles.Clear();
                _visitedCompletionKeys.Clear();
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



