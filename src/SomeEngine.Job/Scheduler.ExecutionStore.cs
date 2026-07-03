using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private readonly struct WorkRelease
    {
        internal readonly bool Started;
        internal readonly ResourceAccessRegistration Accesses;

        internal WorkRelease(bool started, ResourceAccessRegistration accesses)
        {
            Started = started;
            Accesses = accesses;
        }
    }

    private readonly struct WorkCallbacks
    {
        internal readonly List<DependencyContinuation>? Continuations;
        internal readonly ExceptionDispatchInfo? Fault;

        internal WorkCallbacks(
            List<DependencyContinuation>? continuations,
            ExceptionDispatchInfo? fault)
        {
            Continuations = continuations;
            Fault = fault;
        }
    }

    private readonly struct CompleteRelease
    {
        internal readonly bool Started;
        internal readonly List<ScopeOwnedResource>? Resources;

        internal CompleteRelease(bool started, List<ScopeOwnedResource>? resources)
        {
            Started = started;
            Resources = resources;
        }
    }

    private readonly struct CompleteCallbacks
    {
        internal readonly bool Completed;
        internal readonly List<DependencyContinuation>? Continuations;
        internal readonly List<ExternalCompletionContinuation>? ExternalContinuations;
        internal readonly ScopeToken Parent;
        internal readonly ExceptionDispatchInfo? Fault;

        internal CompleteCallbacks(
            bool completed,
            List<DependencyContinuation>? continuations,
            List<ExternalCompletionContinuation>? externalContinuations,
            ScopeToken parent,
            ExceptionDispatchInfo? fault)
        {
            Completed = completed;
            Continuations = continuations;
            ExternalContinuations = externalContinuations;
            Parent = parent;
            Fault = fault;
        }
    }

    private sealed partial class CompletionStore
    {
        public bool CanExecute(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            if (Volatile.Read(ref state.InUse)
                && Volatile.Read(ref state.Version) == handle.Version
                && !Volatile.Read(ref state.Completed)
                && Volatile.Read(ref state.PendingDependencies) == 0)
            {
                return true;
            }

            lock (state.Sync)
            {
                return state.InUse
                    && state.Version == handle.Version
                    && !state.Completed
                    && state.PendingDependencies == 0;
            }
        }

        public bool CompleteItem(JobHandle handle, ExceptionDispatchInfo? itemFault)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || state.Completed)
                {
                    return false;
                }

                if (itemFault is not null)
                {
                    state.Fault ??= itemFault;
                }

                state.PendingWork--;
                return true;
            }
        }

        public WorkRelease BeginWork(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return default;
            }

            lock (state.Sync)
            {
                if (!state.InUse
                    || state.Version != handle.Version
                    || state.WorkDependenciesReleased
                    || state.WorkDependencyReleaseInProgress
                    || state.PendingDependencies != 0
                    || state.PendingWork != 0)
                {
                    return default;
                }

                state.WorkDependencyReleaseInProgress = true;
                ResourceAccessRegistration accesses = state.ResourceAccesses;
                state.ResourceAccesses = ResourceAccessRegistration.Empty;
                return new WorkRelease(started: true, accesses);
            }
        }

        public WorkCallbacks EndWork(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return default;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || !state.WorkDependencyReleaseInProgress)
                {
                    return default;
                }

                state.WorkDependenciesReleased = true;
                state.WorkDependencyReleaseInProgress = false;
                ExceptionDispatchInfo? fault = state.Fault;
                if (state.WorkContinuations.Count == 0)
                {
                    return new WorkCallbacks(null, fault);
                }

                List<DependencyContinuation> continuations = state.WorkContinuationDispatchBuffer;
                continuations.AddRange(state.WorkContinuations);
                state.WorkContinuations.Clear();
                return new WorkCallbacks(continuations, fault);
            }
        }

        public CompleteRelease BeginDispatch(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return default;
            }

            lock (state.Sync)
            {
                if (!state.InUse
                    || state.Version != handle.Version
                    || state.Completed
                    || state.CompletionDispatchInProgress
                    || !state.WorkDependenciesReleased
                    || state.WorkDependencyReleaseInProgress
                    || state.PendingDependencies != 0
                    || state.PendingWork != 0
                    || state.PendingChildren != 0)
                {
                    return default;
                }

                state.CompletionDispatchInProgress = true;
                if (state.ScopeOwnedResources.Count == 0)
                {
                    return new CompleteRelease(started: true, resources: null);
                }

                List<ScopeOwnedResource> resources = state.ScopeOwnedResourceDispatchBuffer;
                resources.AddRange(state.ScopeOwnedResources);
                state.ScopeOwnedResources.Clear();
                return new CompleteRelease(started: true, resources);
            }
        }

        public CompleteCallbacks EndDispatch(JobHandle handle)
        {
            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return default;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || !state.CompletionDispatchInProgress)
                {
                    return default;
                }

                state.Completed = true;
                state.CompletionDispatchInProgress = false;
                ExceptionDispatchInfo? fault = state.Fault;
                ScopeToken parent = state.Parent;
                List<DependencyContinuation>? continuations;
                List<ExternalCompletionContinuation>? externalContinuations;

                if (state.Continuations.Count == 0)
                {
                    continuations = null;
                }
                else
                {
                    continuations = state.ContinuationDispatchBuffer;
                    continuations.AddRange(state.Continuations);
                    state.Continuations.Clear();
                }

                if (state.ExternalContinuations is null || state.ExternalContinuations.Count == 0)
                {
                    externalContinuations = null;
                }
                else
                {
                    state.ExternalContinuationDispatchBuffer ??= [];
                    externalContinuations = state.ExternalContinuationDispatchBuffer;
                    externalContinuations.AddRange(state.ExternalContinuations);
                    state.ExternalContinuations.Clear();
                }

                return new CompleteCallbacks(
                    completed: true,
                    continuations,
                    externalContinuations,
                    parent,
                    fault);
            }
        }

        public bool SetResources(JobHandle handle, ResourceAccessRegistration registration)
        {
            if (registration.AccessCount == 0)
            {
                return true;
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != handle.Version || state.Completed)
                {
                    return false;
                }

                state.ResourceAccesses = registration;
                return true;
            }
        }
    }
}



