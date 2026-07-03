using System.Runtime.ExceptionServices;

namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private sealed partial class CompletionStore
    {
        public bool AddResource(JobHandle owner, ScopeOwnedResource resource)
        {
            CompletionState? state = GetState(owner);
            if (state is null)
            {
                return false;
            }

            lock (state.Sync)
            {
                if (!state.InUse || state.Version != owner.Version || state.Completed)
                {
                    return false;
                }

                state.ScopeOwnedResources.Add(resource);
                return true;
            }
        }

        public JobHandle AttachChild(ScopeToken parent, JobHandle child)
        {
            CompletionState? childState = GetState(child);
            JobHandle parentHandle = parent.ToHandle();
            CompletionState? parentState = GetState(parentHandle);
            if (childState is null || parentState is null)
            {
                return default;
            }

            lock (parentState.Sync)
            {
                if (!parentState.InUse || parentState.Version != parent.Version || parentState.Completed)
                {
                    return default;
                }

                parentState.PendingChildren++;
            }

            lock (childState.Sync)
            {
                if (childState.InUse && childState.Version == child.Version)
                {
                    childState.Parent = parent;
                    return default;
                }
            }

            return ReleaseChild(parent, null) ? parentHandle : default;
        }

        public bool ReleaseChild(ScopeToken parent, ExceptionDispatchInfo? childFault)
        {
            JobHandle parentHandle = parent.ToHandle();
            CompletionState? parentState = GetState(parentHandle);
            if (parentState is null)
            {
                return false;
            }

            lock (parentState.Sync)
            {
                if (!parentState.InUse || parentState.Version != parent.Version || parentState.Completed)
                {
                    return false;
                }

                if (childFault is not null)
                {
                    parentState.Fault ??= childFault;
                }

                parentState.PendingChildren--;
                return true;
            }
        }
    }
}



