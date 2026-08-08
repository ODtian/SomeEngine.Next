namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    internal ResourceAccessRegistration RegisterAccesses(
        JobHandle owner,
        ReadOnlySpan<JobResourceAccess> accesses,
        Type jobType)
    {
        if (accesses.Length == 0)
        {
            return ResourceAccessRegistration.Empty;
        }

        lock (_sync)
        {
            AccessBuilder<ActiveResourceAccess> registered = new();
            try
            {
                foreach (JobResourceAccess access in accesses)
                {
                    ResourceState? state = ResolveForAccess(access, jobType);
                    if (state is null)
                    {
                        continue;
                    }

                    registered.Add(new ActiveResourceAccess(owner, access, state));
                }
            }
            catch
            {
                registered.Clear();
                throw;
            }

            return RegisterResolvedAccesses(registered);
        }
    }

    private ResourceAccessRegistration RegisterResolvedAccesses(
        AccessBuilder<ActiveResourceAccess> registered)
    {
        if (registered.Count == 0)
            return ResourceAccessRegistration.Empty;

        AccessBuilder<ResourceDependency> dependencies = new();
        HashSet<ResourceDependencyKey>? dependencySet = null;
        bool commitStarted = false;
        try
        {
            // Resolve and query the complete declaration before publishing any access owned by
            // this handle. Every slice therefore observes exactly the pre-owner frontier, while
            // the lock keeps the eventual multi-resource commit linearizable.
            for (int i = 0; i < registered.Count; i++)
            {
                ActiveResourceAccess active = registered.Get(i);
                if (CanUseUnrangedFrontier(active.State, active.Access))
                {
                    RegisterUnrangedDependencies(
                        active.State,
                        active.Owner,
                        active.Access,
                        ref dependencies,
                        ref dependencySet);
                }
                else
                {
                    RegisterIndexedDependencies(
                        active.State,
                        active.Owner,
                        active.Access,
                        ref dependencies,
                        ref dependencySet);
                }
            }

            commitStarted = true;
            for (int i = 0; i < registered.Count; i++)
            {
                ActiveResourceAccess active = registered.Get(i);
                active.State.ActiveAccesses.Add(active);
                active.State.AddFrontierAccess(active);
            }

            return CreateRegistration(registered, dependencies);
        }
        catch
        {
            if (commitStarted)
                RemoveRegisteredAccesses(registered);

            registered.Clear();
            dependencies.Clear();
            throw;
        }
        finally
        {
            if (dependencySet is not null)
                ReturnResourceDependencySet(dependencySet);
        }
    }

    internal void ReleaseAccesses(ResourceAccessRegistration registration)
    {
        ResourceAccessRegistrationData? data = registration.Data;
        if (data is null || data.Accesses.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            AccessBuilder<ActiveResourceAccess> accesses = data.Accesses;
            RemoveRegisteredAccesses(accesses);

            data.Clear();
            _freeRegistrations.Push(data);
        }
    }

    private void RemoveRegisteredAccesses(
        AccessBuilder<ActiveResourceAccess> accesses)
    {
        if (accesses.Count <= AccessBuilder<ActiveResourceAccess>.InlineCapacity)
        {
            for (int i = 0; i < accesses.Count; i++)
            {
                ActiveResourceAccess access = accesses.Get(i);
                if (access.State.ActiveAccesses.Remove(access))
                    access.State.RemoveFrontierAccess(access);
            }
            return;
        }

        // One registration belongs to one handle. Remove all of that handle's slices from each
        // touched resource in one linear pass, then rebuild its compact frontier once.
        Dictionary<ResourceState, int> touched = RentResourceStateMap();
        try
        {
            for (int i = 0; i < accesses.Count; i++)
            {
                ActiveResourceAccess access = accesses.Get(i);
                if (touched.TryAdd(access.State, 0))
                    access.State.RemoveOwnerAccesses(access.Owner);
            }
        }
        finally
        {
            ReturnResourceStateMap(touched);
        }
    }

    private ResourceAccessRegistration CreateRegistration(
        AccessBuilder<ActiveResourceAccess> accesses,
        AccessBuilder<ResourceDependency> dependencies)
    {
        ResourceAccessRegistrationData data = _freeRegistrations.Count == 0
            ? new ResourceAccessRegistrationData()
            : _freeRegistrations.Pop();
        try
        {
            data.Reset(accesses, dependencies);
            return new ResourceAccessRegistration(data);
        }
        catch
        {
            _freeRegistrations.Push(data);
            throw;
        }
    }

}



