namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const string InvalidManagedPayloadPolicyMessage = "Managed payload policy is invalid.";
    private const string StaleScopeRuntimeMessage = "Cannot create work or resources from a job scope that belongs to a previous runtime generation.";
    private void ApplyManagedPayloadPolicy<T>(JobPayloadLane lane)
        where T : struct
    {
        if (lane == JobPayloadLane.RefFree)
        {
            return;
        }

        switch (ManagedPayloadPolicy)
        {
            case ManagedPayloadPolicy.Allow:
                return;
            case ManagedPayloadPolicy.Warn:
                _counters.ManagedPayloadWarning();
                return;
            case ManagedPayloadPolicy.Reject:
                throw new InvalidOperationException(
                    $"Managed/ref-containing job payload '{typeof(T).FullName}' is rejected by the managed payload policy.");
            default:
                throw new InvalidOperationException(InvalidManagedPayloadPolicyMessage);
        }
    }

    private void ReleaseAccessesIfRegistered(ResourceAccessRegistration registration)
    {
        if (registration.Data is not null)
        {
            _resources.ReleaseAccesses(registration);
        }
    }

    private void EnsureCurrentScopeBelongsToThisRuntime()
    {
        ScopeToken scope = s_currentScope;
        if (scope.Index != 0 && scope.Generation != Generation)
        {
            throw new InvalidOperationException(
                StaleScopeRuntimeMessage);
        }
    }

    private void SetResourceAccesses(JobHandle handle, ResourceAccessRegistration registration)
    {
        if (registration.AccessCount == 0)
        {
            return;
        }

        if (!_execution.SetResources(handle, registration))
        {
            ReleaseAccessesIfRegistered(registration);
        }
    }

    internal JobResourceToken GetContainerResourceToken(object container)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        return _resources.GetContainerResourceToken(container);
    }

}



