namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    private void AddDependency(
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceDependencyKey>? dependencySet,
        JobHandle handle,
        bool waitForWorkOnly)
    {
        var key = new ResourceDependencyKey(
            handle.Index,
            handle.Version,
            handle.Generation,
            waitForWorkOnly);
        if (dependencySet is not null)
        {
            if (dependencySet.Add(key))
                dependencies.Add(new ResourceDependency(handle, waitForWorkOnly));
            return;
        }

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

        if (dependencies.Count == AccessBuilder<ResourceDependency>.InlineCapacity)
        {
            HashSet<ResourceDependencyKey> rented = RentResourceDependencySet();
            try
            {
                for (int i = 0; i < dependencies.Count; i++)
                {
                    ResourceDependency dependency = dependencies.Get(i);
                    rented.Add(new ResourceDependencyKey(
                        dependency.Handle.Index,
                        dependency.Handle.Version,
                        dependency.Handle.Generation,
                        dependency.WaitForWorkOnly));
                }

                rented.Add(key);
                dependencySet = rented;
            }
            catch
            {
                ReturnResourceDependencySet(rented);
                throw;
            }
        }

        dependencies.Add(new ResourceDependency(handle, waitForWorkOnly));
    }

    private static bool CanUseUnrangedFrontier(ResourceState state, JobResourceAccess access)
    {
        return !access.HasRange && state.RangedAccessCount == 0;
    }

    private int RegisterUnrangedDependencies(
        ResourceState state,
        JobHandle owner,
        JobResourceAccess access,
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceDependencyKey>? dependencySet)
    {
        int steps = 0;
        if (state.HasLastUnrangedWriter)
        {
            steps++;
            AddDependency(
                ref dependencies,
                ref dependencySet,
                state.LastUnrangedWriter,
                owner);
        }

        if (access.Mode == JobAccessMode.Read)
            return steps;

        foreach (ActiveResourceAccess active in state.UnrangedReadersSinceLastWriter)
        {
            steps++;
            AddDependency(ref dependencies, ref dependencySet, active, owner);
        }

        return steps;
    }

    private void RegisterIndexedDependencies(
        ResourceState state,
        JobHandle owner,
        JobResourceAccess access,
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceDependencyKey>? dependencySet)
    {
        int steps = RegisterUnrangedDependencies(
            state,
            owner,
            access,
            ref dependencies,
            ref dependencySet);
        RangedResourceFrontier ranged = access.Mode == JobAccessMode.Read
            ? state.RangedWriters
            : state.RangedAll;
        steps += ranged.AddDependencies(
            this,
            access,
            owner,
            ref dependencies,
            ref dependencySet);
        _counters.ResourceConflictCheck(steps);
    }

    internal void AddDependency(
        ref AccessBuilder<ResourceDependency> dependencies,
        ref HashSet<ResourceDependencyKey>? dependencySet,
        ActiveResourceAccess active,
        JobHandle owner)
    {
        JobHandle dependency = active.Owner;
        if (dependency.Index == owner.Index
            && dependency.Version == owner.Version
            && dependency.Generation == owner.Generation)
        {
            return;
        }

        AddDependency(
            ref dependencies,
            ref dependencySet,
            dependency,
            // A resource grant belongs to the owner's work body. Attached children extend the
            // handle's lifetime, but they do not extend that grant: ReleaseAccesses runs as soon
            // as PendingWork reaches zero. Waiting for full completion here can therefore create
            // a ring when an attached child needs a resource already registered by the successor.
            waitForWorkOnly: true);
    }

    internal readonly record struct ResourceDependencyKey(
        int Index,
        int Version,
        long Generation,
        bool WaitForWorkOnly);
}
