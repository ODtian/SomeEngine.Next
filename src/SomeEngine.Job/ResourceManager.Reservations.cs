namespace SomeEngine.Job;

internal sealed partial class ResourceManager
{
    internal ResourceAccessReservation ReserveAccesses(
        ReadOnlySpan<JobResourceAccess> accesses,
        Type jobType)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(JobTrace.ReserveResources);
        if (accesses.Length == 0)
            return ResourceAccessReservation.Empty;

        lock (_sync)
        {
            ResourceAccessReservationData data = _freeReservations.Count == 0
                ? new ResourceAccessReservationData() : _freeReservations.Pop();
            if (data.Accesses.Length < accesses.Length)
                data.Accesses = new ReservedResourceAccess[accesses.Length];
            data.JobType = jobType;
            data.References = 1;
            data.Status = ResourceReservationStatus.Reserved;
            try
            {
                foreach (JobResourceAccess access in accesses)
                {
                    ResourceState? state = ResolveForAccess(access, jobType);
                    if (state is null)
                        continue;

                    state.PendingReservations = checked(state.PendingReservations + 1);
                    data.Accesses[data.Count++] = new ReservedResourceAccess(access, state);
                }
            }
            catch
            {
                ConsumeReservation(data, ResourceReservationStatus.Cancelled);
                ReleaseReservationReference(new(data));
                throw;
            }
            if (data.Count != 0)
                return new(data);
            ReleaseReservationReference(new(data));
            return ResourceAccessReservation.Empty;
        }
    }

    internal static void AddReservationReference(ResourceAccessReservation reservation)
    {
        if (reservation.Data is { } data)
            Interlocked.Increment(ref data.References);
    }

    internal void ReleaseReservationReference(ResourceAccessReservation reservation)
    {
        if (reservation.Data is not { } data || Interlocked.Decrement(ref data.References) != 0)
            return;
        lock (_sync)
        {
            data.JobType = null!;
            _freeReservations.Push(data);
        }
    }

    internal ResourceAccessRegistration ActivateReservation(
        JobHandle owner,
        ResourceAccessReservation reservation)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(JobTrace.ActivateResources);
        ResourceAccessReservationData? data = reservation.Data;
        if (data is null)
            return ResourceAccessRegistration.Empty;

        lock (_sync)
        {
            if (data.Status != ResourceReservationStatus.Reserved)
            {
                throw new InvalidOperationException(
                    "A deferred resource reservation can be activated only once.");
            }

            AccessBuilder<ActiveResourceAccess> registered = new();
            try
            {
                for (int i = 0; i < data.Count; i++)
                {
                    ReservedResourceAccess reserved = data.Accesses[i];
                    ResourceState? state = ResolveForAccess(reserved.Access, data.JobType);
                    if (state is null)
                        continue;

                    registered.Add(new ActiveResourceAccess(owner, reserved.Access, state));
                }
            }
            catch
            {
                registered.Clear();
                throw;
            }

            ResourceAccessRegistration registration = RegisterResolvedAccesses(registered);
            ConsumeReservation(data, ResourceReservationStatus.Activated);
            return registration;
        }
    }

    internal void CancelReservation(ResourceAccessReservation reservation)
    {
        ResourceAccessReservationData? data = reservation.Data;
        if (data is null)
            return;

        lock (_sync)
        {
            if (data.Status == ResourceReservationStatus.Reserved)
                ConsumeReservation(data, ResourceReservationStatus.Cancelled);
        }
    }

    private static void ConsumeReservation(
        ResourceAccessReservationData data,
        ResourceReservationStatus status)
    {
        ReleaseReservationCounts(data.Accesses, data.Count);
        Array.Clear(data.Accesses, 0, data.Count);
        data.Count = 0;
        data.Status = status;
    }

    private static void ReleaseReservationCounts(
        ReservedResourceAccess[] accesses,
        int count)
    {
        for (int i = 0; i < count; i++)
        {
            ReservedResourceAccess reserved = accesses[i];
            ResourceState state = reserved.State;
            JobResourceAccess access = reserved.Access;
            if (!state.InUse ||
                state.Id != access.Id ||
                state.Version != access.Version ||
                state.Kind != access.Kind ||
                state.PendingReservations <= 0)
            {
                continue;
            }

            state.PendingReservations--;
        }
    }

    internal readonly struct ReservedResourceAccess
    {
        internal ReservedResourceAccess(JobResourceAccess access, ResourceState state)
        {
            Access = access;
            State = state;
            Container = state.ContainerReference?.Target;
        }

        internal JobResourceAccess Access { get; }

        internal ResourceState State { get; }
        internal object? Container { get; }
    }
}
