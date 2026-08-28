using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private sealed class CompletionState
    {
        internal readonly Lock Sync = new();
        internal int Version;
        internal bool InUse;
        internal bool Completed;
        internal int PendingWork;
        internal int PendingDependencies;
        internal int PendingChildren;
        internal int ActiveCompleters;
        internal ScopeToken Parent;
        internal ExceptionDispatchInfo? Fault;
        internal bool FaultObserved;
        internal bool QueuedForFaultReuse;
        internal List<DependencyContinuation> Continuations { get; } = [];
        internal List<DependencyContinuation> ContinuationDispatchBuffer { get; } = [];
        internal List<ExternalCompletionContinuation>? ExternalContinuations { get; set; }
        internal List<ExternalCompletionContinuation>? ExternalContinuationDispatchBuffer { get; set; }
        internal bool ScheduleDependenciesSealed;
        internal bool WorkDependenciesReleased;
        internal bool WorkDependencyReleaseInProgress;
        internal bool CompletionDispatchInProgress;
        internal bool MayExecuteConcurrently;
        internal List<DependencyContinuation> WorkContinuations { get; } = [];
        internal List<DependencyContinuation> WorkContinuationDispatchBuffer { get; } = [];
        internal ResourceAccessRegistration ResourceAccesses { get; set; } = ResourceAccessRegistration.Empty;
        internal List<ScopeOwnedResource> ScopeOwnedResources { get; } = [];
        internal List<ScopeOwnedResource> ScopeOwnedResourceDispatchBuffer { get; } = [];

        internal void RecordFirstFault(ExceptionDispatchInfo fault)
        {
            ArgumentNullException.ThrowIfNull(fault);
            Interlocked.CompareExchange(ref Fault, fault, null);
        }

        internal void Reset(int pendingWork, int pendingDependencies, bool scheduleDependenciesSealed)
        {
            Version++;
            InUse = true;
            Completed = false;
            PendingWork = pendingWork;
            PendingDependencies = pendingDependencies;
            PendingChildren = 0;
            ActiveCompleters = 0;
            Parent = default;
            Fault = null;
            FaultObserved = false;
            QueuedForFaultReuse = false;
            ClearIfNotEmpty(Continuations);
            ClearIfNotEmpty(ContinuationDispatchBuffer);
            ClearIfNotEmpty(ExternalContinuations);
            ClearIfNotEmpty(ExternalContinuationDispatchBuffer);
            ScheduleDependenciesSealed = scheduleDependenciesSealed;
            WorkDependenciesReleased = pendingWork == 0;
            WorkDependencyReleaseInProgress = false;
            CompletionDispatchInProgress = false;
            MayExecuteConcurrently = pendingWork > 1;
            ClearIfNotEmpty(WorkContinuations);
            ClearIfNotEmpty(WorkContinuationDispatchBuffer);
            ResourceAccesses = ResourceAccessRegistration.Empty;
            ClearIfNotEmpty(ScopeOwnedResources);
            ClearIfNotEmpty(ScopeOwnedResourceDispatchBuffer);
        }

        internal void Release()
        {
            InUse = false;
            Completed = true;
            PendingWork = 0;
            PendingDependencies = 0;
            PendingChildren = 0;
            ActiveCompleters = 0;
            Parent = default;
            Fault = null;
            FaultObserved = false;
            QueuedForFaultReuse = false;
            ClearIfNotEmpty(Continuations);
            ClearIfNotEmpty(ContinuationDispatchBuffer);
            ClearIfNotEmpty(ExternalContinuations);
            ClearIfNotEmpty(ExternalContinuationDispatchBuffer);
            ScheduleDependenciesSealed = true;
            WorkDependenciesReleased = true;
            WorkDependencyReleaseInProgress = false;
            CompletionDispatchInProgress = false;
            MayExecuteConcurrently = false;
            ClearIfNotEmpty(WorkContinuations);
            ClearIfNotEmpty(WorkContinuationDispatchBuffer);
            ResourceAccesses = ResourceAccessRegistration.Empty;
            ClearIfNotEmpty(ScopeOwnedResources);
            ClearIfNotEmpty(ScopeOwnedResourceDispatchBuffer);
        }

        private static void ClearIfNotEmpty<T>(List<T>? list)
        {
            if (list is not null && list.Count != 0)
            {
                list.Clear();
            }
        }
    }
}



