namespace SomeEngine.Job;

internal sealed partial class Scheduler
{
    private const string InvalidManagedPayloadPolicyMessage = "Managed payload policy is invalid.";
    private const string StaleScopeRuntimeMessage = "Cannot create work or resources from a job scope that belongs to a previous runtime generation.";
    private const string CurrentJobRequiredMessage = "A running job scope is required to use this resource capability.";
    private const string ConcurrentCapabilityMessage = "This resource capability requires a single-work-item job owner and cannot be used by a concurrently executing parallel job.";
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

    private ResourceAccessReservation ReserveAccesses<T>(
        ReadOnlySpan<JobResourceAccess> accesses)
        where T : struct
    {
        return _resources.ReserveAccesses(accesses, typeof(T));
    }

    private void CancelReservation(ResourceAccessReservation reservation)
    {
        _resources.CancelReservation(reservation);
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

    private bool SetResourceAccesses(JobHandle handle, ResourceAccessRegistration registration)
    {
        if (registration.AccessCount == 0)
        {
            return true;
        }

        if (!_execution.SetResources(handle, registration))
        {
            ReleaseAccessesIfRegistered(registration);
            return false;
        }

        return true;
    }

    internal void RequireCurrentAccess(JobResourceAccess required, bool requireSingleWorkItem)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        ScopeToken scope = s_currentScope;
        if (scope.Index == 0)
        {
            throw CreateCurrentAccessException(required, CurrentJobRequiredMessage);
        }

        if (!_execution.HasResourceAccess(
                scope.ToHandle(),
                required,
                out bool mayExecuteConcurrently))
        {
            throw CreateCurrentAccessException(
                required,
                $"The current job did not declare a covering {Describe(required)} access.");
        }

        if (requireSingleWorkItem && mayExecuteConcurrently)
        {
            throw CreateCurrentAccessException(required, ConcurrentCapabilityMessage);
        }
    }

    private JobResourceSafetyException CreateCurrentAccessException(
        JobResourceAccess required,
        string message)
    {
        return new JobResourceSafetyException(
            message,
            _resources.SafetyMode,
            jobTypeName: null,
            resourceName: null,
            required.Id,
            required.Kind.ToString());
    }

    private static string Describe(JobResourceAccess access)
    {
        string mode = access.Mode.ToString().ToLowerInvariant();
        return access.HasRange
            ? $"{mode} range [{access.RangeStart}, {access.RangeStart + access.RangeLength})"
            : mode;
    }

    internal JobResourceToken GetContainerResourceToken(object container)
    {
        EnsureCurrentScopeBelongsToThisRuntime();
        JobResourceToken token = _resources.GetContainerResourceToken(container);
        if (container is JobResourceKey { SubmissionObserver: not null } key)
        {
            var identity = new SubmissionResourceIdentity(
                ResourceKind.Token,
                token.Id,
                token.Version,
                token.Generation);
            lock (_submissionObserverGate)
                _submissionObservers[identity] = key.SubmissionObserver;
        }

        return token;
    }

}



