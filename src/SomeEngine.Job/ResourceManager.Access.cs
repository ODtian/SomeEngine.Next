namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    internal ResourceAccessRegistration RegisterAccesses(
        JobHandle owner,
        ReadOnlySpan<JobResourceAccess> accesses,
        Type jobType,
        JobHandle currentScope)
    {
        if (accesses.Length == 0)
        {
            return ResourceAccessRegistration.Empty;
        }

        lock (_sync)
        {
            AccessBuilder<ActiveResourceAccess> registered = new();
            AccessBuilder<ResourceDependency> dependencies = new();

            try
            {
                foreach (JobResourceAccess access in accesses)
                {
                    ResourceState? state = ResolveForAccess(access, jobType);
                    if (state is null)
                    {
                        continue;
                    }

                    if (CanUseUnrangedFrontier(state, access))
                    {
                        RegisterUnrangedDependencies(state, owner, access, currentScope, ref dependencies);
                    }
                    else
                    {
                        RegisterScannedDependencies(state, owner, access, currentScope, ref dependencies);
                    }

                    ActiveResourceAccess resourceAccess = new(owner, access, state);
                    registered.Add(resourceAccess);
                    state.ActiveAccesses.Add(resourceAccess);
                    state.AddFrontierAccess(resourceAccess);
                }
            }
            catch
            {
                for (int i = 0; i < registered.Count; i++)
                {
                    ActiveResourceAccess access = registered.Get(i);
                    if (access.State.ActiveAccesses.Remove(access))
                    {
                        access.State.RemoveFrontierAccess(access);
                    }
                }

                registered.Clear();
                dependencies.Clear();
                throw;
            }

            if (registered.Count == 0 && dependencies.Count == 0)
            {
                return ResourceAccessRegistration.Empty;
            }

            return CreateRegistration(registered, dependencies);
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
            for (int i = 0; i < accesses.Count; i++)
            {
                ActiveResourceAccess access = accesses.Get(i);
                if (access.State.ActiveAccesses.Remove(access))
                {
                    access.State.RemoveFrontierAccess(access);
                }
            }

            data.Clear();
            _freeRegistrations.Push(data);
        }
    }

    private ResourceAccessRegistration CreateRegistration(
        AccessBuilder<ActiveResourceAccess> accesses,
        AccessBuilder<ResourceDependency> dependencies)
    {
        ResourceAccessRegistrationData data = _freeRegistrations.Count == 0
            ? new ResourceAccessRegistrationData()
            : _freeRegistrations.Pop();

        data.Reset(accesses, dependencies);
        return new ResourceAccessRegistration(data);
    }

}



