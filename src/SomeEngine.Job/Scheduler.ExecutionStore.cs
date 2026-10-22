using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

public sealed partial class Scheduler
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
        internal readonly DependencyContinuation[]? Continuations;
        internal readonly ExceptionDispatchInfo? Fault;

        internal WorkCallbacks(
            DependencyContinuation[]? continuations,
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
        internal readonly DependencyContinuation[]? Continuations;
        internal readonly ExternalCompletionContinuation[]? ExternalContinuations;
        internal readonly CompletionLease ExternalContinuationLease;
        internal readonly ScopeToken Parent;
        internal readonly ExceptionDispatchInfo? Fault;

        internal CompleteCallbacks(
            bool completed,
            DependencyContinuation[]? continuations,
            ExternalCompletionContinuation[]? externalContinuations,
            CompletionLease externalContinuationLease,
            ScopeToken parent,
            ExceptionDispatchInfo? fault)
        {
            Completed = completed;
            Continuations = continuations;
            ExternalContinuations = externalContinuations;
            ExternalContinuationLease = externalContinuationLease;
            Parent = parent;
            Fault = fault;
        }
    }

    private sealed partial class CompletionStore
    {
        public void AddWorkItems(JobHandle handle, int count)
        {
            CompletionState state = GetState(handle)!;
            Volatile.Write(ref state.MayExecuteConcurrently, true);
            Interlocked.Add(ref state.PendingWork, count);
        }

        public SomeEngine.Diagnostics.TraceContext GetTraceContext(JobHandle handle)
            => GetState(handle)?.TraceContext ?? default;

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

        public bool CompleteItems(
            JobHandle handle,
            int completedWorkItems,
            ExceptionDispatchInfo? itemFault,
            out bool workFinished)
        {
            workFinished = false;
            if (completedWorkItems <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedWorkItems));
            }

            CompletionState? state = GetState(handle);
            if (state is null)
            {
                return false;
            }

            // PendingWork accounts for every published logical work item. A state cannot be
            // completed or recycled while one of those items still owns its count, so the hot
            // completion path needs no state monitor. The last finisher alone enters the locked
            // resource/continuation dispatch path through CompleteStreamItems.
            if (!Volatile.Read(ref state.InUse)
                || Volatile.Read(ref state.Version) != handle.Version
                || Volatile.Read(ref state.Completed))
            {
                return false;
            }

            if (itemFault is not null)
            {
                state.RecordFirstFault(itemFault);
            }

            int remaining = Interlocked.Add(ref state.PendingWork, -completedWorkItems);
            if (remaining < 0)
            {
                throw new InvalidOperationException("Completion state pending-work count underflowed.");
            }

            workFinished = remaining == 0;
            return true;
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

                DependencyContinuation[] continuations = state.WorkContinuations.ToArray();
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
                DependencyContinuation[]? continuations;
                ExternalCompletionContinuation[]? externalContinuations;
                CompletionLease externalContinuationLease = default;

                if (state.Continuations.Count == 0)
                {
                    continuations = null;
                }
                else
                {
                    continuations = state.Continuations.ToArray();
                    state.Continuations.Clear();
                }

                if (state.ExternalContinuations is null || state.ExternalContinuations.Count == 0)
                {
                    externalContinuations = null;
                }
                else
                {
                    externalContinuations = state.ExternalContinuations.ToArray();
                    state.ExternalContinuations.Clear();
                    state.ActiveCompleters++;
                    externalContinuationLease = new CompletionLease(state, handle);
                }

                return new CompleteCallbacks(
                    completed: true,
                    continuations,
                    externalContinuations,
                    externalContinuationLease,
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

        public bool HasResourceAccess(
            JobHandle handle,
            JobResourceAccess required,
            out bool mayExecuteConcurrently)
        {
            mayExecuteConcurrently = false;
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

                mayExecuteConcurrently = state.MayExecuteConcurrently;
                ResourceAccessRegistration registration = state.ResourceAccesses;
                return registration.Covers(required);
            }
        }
    }
}



