namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private static void AddDependency(
        ref AccessBuilder<ResourceDependency> dependencies,
        JobHandle handle,
        bool waitForWorkOnly)
    {
        for (int i = 0; i < dependencies.Count; i++)
        {
            ResourceDependency dependency = dependencies.Get(i);
            if (dependency.Handle.Index == handle.Index
                && dependency.Handle.Version == handle.Version
                && dependency.Handle.Generation == handle.Generation
                && dependency.WaitForWorkOnly == waitForWorkOnly)
            {
                return;
            }
        }

        dependencies.Add(new ResourceDependency(handle, waitForWorkOnly));
    }

    private static bool CanUseUnrangedFrontier(ResourceState state, JobResourceAccess access)
    {
        return !access.HasRange && state.RangedAccessCount == 0;
    }

    private static void RegisterUnrangedDependencies(
        ResourceState state,
        JobHandle owner,
        JobResourceAccess access,
        JobHandle currentScope,
        ref AccessBuilder<ResourceDependency> dependencies)
    {
        if (state.HasLastUnrangedWriter)
        {
            AddDependency(ref dependencies, state.LastUnrangedWriter, owner, currentScope);
        }

        if (access.Mode == JobAccessMode.Read)
        {
            return;
        }

        foreach (ActiveResourceAccess active in state.UnrangedReadersSinceLastWriter)
        {
            AddDependency(ref dependencies, active, owner, currentScope);
        }
    }

    private void RegisterScannedDependencies(
        ResourceState state,
        JobHandle owner,
        JobResourceAccess access,
        JobHandle currentScope,
        ref AccessBuilder<ResourceDependency> dependencies)
    {
        int steps = 0;
        foreach (ActiveResourceAccess active in state.ActiveAccesses)
        {
            steps++;
            if (!Conflicts(active.Access, access))
            {
                continue;
            }

            AddDependency(ref dependencies, active, owner, currentScope);
        }

        _counters.ResourceConflictCheck(steps);
    }

    private static void AddDependency(
        ref AccessBuilder<ResourceDependency> dependencies,
        ActiveResourceAccess active,
        JobHandle owner,
        JobHandle currentScope)
    {
        AddDependency(ref dependencies, active.Owner, owner, currentScope);
    }

    private static void AddDependency(
        ref AccessBuilder<ResourceDependency> dependencies,
        JobHandle dependency,
        JobHandle owner,
        JobHandle currentScope)
    {
        if (dependency.Index == owner.Index
            && dependency.Version == owner.Version
            && dependency.Generation == owner.Generation)
        {
            return;
        }

        AddDependency(
            ref dependencies,
            dependency,
            currentScope.Index == dependency.Index
                && currentScope.Version == dependency.Version
                && currentScope.Generation == dependency.Generation);
    }

    private static bool Conflicts(JobResourceAccess existing, JobResourceAccess candidate)
    {
        if (!RangesOverlap(existing, candidate))
        {
            return false;
        }

        if (existing.Mode == JobAccessMode.Read && candidate.Mode == JobAccessMode.Read)
        {
            return false;
        }

        return true;
    }

    private static bool RangesOverlap(JobResourceAccess existing, JobResourceAccess candidate)
    {
        if (!existing.HasRange || !candidate.HasRange)
        {
            return true;
        }

        long existingEnd = existing.RangeStart + existing.RangeLength;
        long candidateEnd = candidate.RangeStart + candidate.RangeLength;
        return existing.RangeStart < candidateEnd && candidate.RangeStart < existingEnd;
    }

}



